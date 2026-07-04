using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 墓碑内存缓存实现。
///
/// 内部维护两个 ConcurrentDictionary：
/// - _frnCache：以 FRN 键（"{VolumeGuid}:{FRN}"）索引，用于精确匹配
/// - _fingerprintCache：以指纹键（"{Size}:{LastWriteTime:O}"）索引，用于降级匹配
///
/// 启动时通过 ILocalTombstoneRepository.GetAllAsync() 全量加载。
/// 新增墓碑时同时更新内存缓存（由调用方负责同时写入数据库）。
/// 回滚时通过 RemoveByOperationId 精确清除关联记录。
/// </summary>
public sealed class TombstoneCache : ITombstoneCache
{
    // FRN 可用时使用此字典（精确匹配，无过期限制）
    private readonly ConcurrentDictionary<string, LocalTombstone> _frnCache = new();

    // FRN 不可用时使用此字典（指纹匹配，强制 3 天过期）
    private readonly ConcurrentDictionary<string, LocalTombstone> _fingerprintCache = new();

    /// <summary>
    /// ISO 8601 日期时间格式正则，用于精确区分 FRN 键与指纹键。
    ///
    /// FRN 键格式: "{VolumeGuid}:{FileReferenceNumber}"，不含日期时间。
    /// 指纹键格式: "{Size}:{LastWriteTime:O}"，包含 ISO 8601 格式的时间戳（如 "2026-06-21T10:30:00.0000000"）。
    ///
    /// 通过检测 "YYYY-MM-DDTHH:MM:SS" 模式来精确识别指纹键，
    /// 替代此前 Contains('T') 的启发式判断（A3 审计偏差 #2 修复）。
    /// </summary>
    private static readonly Regex Iso8601Pattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILocalTombstoneRepository _repository;
    private readonly ILogger<TombstoneCache> _logger;
    private volatile bool _loaded;

    public int Count => _frnCache.Count + _fingerprintCache.Count;

    public TombstoneCache(
        ILocalTombstoneRepository repository,
        ILogger<TombstoneCache> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            _logger.LogWarning("墓碑缓存已加载，跳过重复加载");
            return;
        }

        var allTombstones = await _repository.GetAllAsync(ct).ConfigureAwait(false);

        int frnCount = 0;
        int fingerprintCount = 0;

        foreach (var tombstone in allTombstones)
        {
            AddToCache(tombstone, ref frnCount, ref fingerprintCount);
        }

        _loaded = true;
        _logger.LogInformation(
            "墓碑缓存加载完成: 总计 {Total} 条 (FRN:{FrnCount}, 指纹:{FpCount})",
            allTombstones.Count, frnCount, fingerprintCount);
    }

    /// <inheritdoc/>
    public bool IsTombstoned(string? frnKey, string? fingerprintKey,
        string? filePath = null, long? fileSize = null)
    {
        // 优先使用 FRN 精确匹配（FRN 是 NTFS 物理标识，无碰撞风险）
        if (frnKey is not null && _frnCache.ContainsKey(frnKey))
        {
            return true;
        }

        // 回退到指纹降级匹配 + 额外校验
        if (fingerprintKey is not null && _fingerprintCache.TryGetValue(fingerprintKey, out var tombstone))
        {
            // 若调用方提供了路径和大小，执行额外校验防止指纹碰撞
            if (filePath is not null && fileSize.HasValue)
            {
                bool pathMatch = string.Equals(tombstone.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
                bool sizeMatch = tombstone.OriginalSize == fileSize.Value;

                if (!pathMatch || !sizeMatch)
                {
                    // 指纹碰撞：键匹配但路径或大小不一致，视为未命中
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Add(LocalTombstone tombstone)
    {
        int frnCount = 0;
        int fingerprintCount = 0;
        AddToCache(tombstone, ref frnCount, ref fingerprintCount);
    }

    /// <inheritdoc/>
    public void AddBatch(IEnumerable<LocalTombstone> tombstones)
    {
        int frnCount = 0;
        int fingerprintCount = 0;

        foreach (var tombstone in tombstones)
        {
            AddToCache(tombstone, ref frnCount, ref fingerprintCount);
        }

        _logger.LogDebug(
            "批量添加墓碑到缓存: FRN={FrnCount}, 指纹={FpCount}",
            frnCount, fingerprintCount);
    }

    /// <inheritdoc/>
    public void RemoveByOperationId(string operationId)
    {
        int removedFrn = 0;
        int removedFp = 0;

        // 从 FRN 缓存中移除
        foreach (var kvp in _frnCache)
        {
            if (kvp.Value.OperationId == operationId && _frnCache.TryRemove(kvp.Key, out _))
            {
                removedFrn++;
            }
        }

        // 从指纹缓存中移除
        foreach (var kvp in _fingerprintCache)
        {
            if (kvp.Value.OperationId == operationId && _fingerprintCache.TryRemove(kvp.Key, out _))
            {
                removedFp++;
            }
        }

        _logger.LogInformation(
            "[墓碑缓存] 按操作 ID 批量移除: OpId={OpId}, FRN={FrnCount}, 指纹={FpCount}",
            operationId, removedFrn, removedFp);
    }

    /// <inheritdoc/>
    public int RemoveWhere(Func<LocalTombstone, bool> predicate)
    {
        int removedFrn = 0;
        int removedFp = 0;

        foreach (var kvp in _frnCache)
        {
            if (predicate(kvp.Value) && _frnCache.TryRemove(kvp.Key, out _))
            {
                removedFrn++;
            }
        }

        foreach (var kvp in _fingerprintCache)
        {
            if (predicate(kvp.Value) && _fingerprintCache.TryRemove(kvp.Key, out _))
            {
                removedFp++;
            }
        }

        int total = removedFrn + removedFp;
        if (total > 0)
        {
            _logger.LogInformation(
                "[墓碑缓存] 条件移除: FRN={FrnCount}, 指纹={FpCount}, 总计={Total}",
                removedFrn, removedFp, total);
        }

        return total;
    }

    /// <summary>
    /// 将单条墓碑添加到对应的缓存字典中。
    /// 根据 FileIdentityKey 的格式自动判断应放入 FRN 缓存还是指纹缓存：
    /// - 匹配 ISO 8601 日期时间模式 → 指纹键（降级匹配，强制 3 天过期）
    /// - 不匹配 → FRN 键（精确匹配，无过期限制）
    /// </summary>
    private void AddToCache(
        LocalTombstone tombstone, ref int frnCount, ref int fingerprintCount)
    {
        // FRN 键格式: "{VolumeGuid}:{FileReferenceNumber}"，不含日期时间
        // 指纹键格式: "{Size}:{LastWriteTime:O}"，必然包含 ISO 8601 时间戳
        // 通过正则精确检测 "YYYY-MM-DDTHH:MM:SS" 模式来区分
        if (Iso8601Pattern.IsMatch(tombstone.FileIdentityKey))
        {
            _fingerprintCache[tombstone.FileIdentityKey] = tombstone;
            fingerprintCount++;
        }
        else
        {
            _frnCache[tombstone.FileIdentityKey] = tombstone;
            frnCount++;
        }
    }
}
