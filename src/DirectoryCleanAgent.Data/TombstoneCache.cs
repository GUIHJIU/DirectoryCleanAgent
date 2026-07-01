using System.Collections.Concurrent;
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
    public bool IsTombstoned(string? frnKey, string? fingerprintKey)
    {
        // 优先使用 FRN 精确匹配
        if (frnKey is not null && _frnCache.ContainsKey(frnKey))
        {
            return true;
        }

        // 回退到指纹降级匹配
        if (fingerprintKey is not null && _fingerprintCache.ContainsKey(fingerprintKey))
        {
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
    /// - 包含 ":" 且不含 "T"（ISO 日期分隔符）→ FRN 键
    /// - 否则 → 指纹键
    /// </summary>
    private void AddToCache(
        LocalTombstone tombstone, ref int frnCount, ref int fingerprintCount)
    {
        // FRN 键格式: "{VolumeGuid}:{FRN}"（包含冒号）
        // 指纹键格式: "{Size}:{LastWriteTime:O}"（包含 ISO 日期时间，含 T 分隔符）
        // 区分方式：指纹键必然包含日期时间的 "T"（如 "2026-06-21T10:30:00"）
        if (tombstone.FileIdentityKey.Contains('T'))
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
