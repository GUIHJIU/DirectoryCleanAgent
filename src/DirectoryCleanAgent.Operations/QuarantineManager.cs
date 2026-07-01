using System.Diagnostics;
using System.Globalization;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Core.PathHandling;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Operations;

/// <summary>
/// 隔离区管理器实现 — B6 阶段核心组件。
///
/// 集中管理所有隔离区关注点，消除 B4（OperationExecutor）和 B5（BackupManager）
/// 中的重复逻辑。
///
/// 职责（设计文档 V3.7 第 7.4 节）：
/// - 目录解析：基于 UserConfig.DataStoragePath + quarantine/ 计算路径并自动创建
/// - 空间监控：枚举 .quarantine 文件，计算总大小、文件数、过期数
/// - 容量强制：检查当前已用空间 + 新增字节 ≤ QuarantineMaxSizeBytes
/// - 大文件绕过：文件 > 隔离区卷可用空间 20% 时跳过隔离区
/// - 过期清理：从文件名解析 Unix 时间戳，与 QuarantineRetentionDays 比较
/// - 批量操作：支持过期清理和全量清空，每个文件 try-catch 隔离故障
///
/// 与 B4/B5 共享的隔离区文件命名规范：
/// {SHA256前16位}_{原始文件名}_{Unix时间戳秒}.quarantine
///
/// 线程安全说明：
/// 单次方法内的枚举和删除是原子的；多实例并发操作同一目录时，
/// 每个文件级别的 try-catch 保证单个文件失败不扩散。
/// 过期清理仅匹配 .quarantine 文件，与 OperationExecutor 的
/// .cleaning.tmp 临时文件不产生交叉干扰。
/// </summary>
public sealed class QuarantineManager : IQuarantineManager
{
    private readonly IConfigService _configService;
    private readonly ILogger<QuarantineManager> _logger;

    /// <summary>隔离区目录名（与 B4/B5 保持一致）</summary>
    internal const string QuarantineDirName = "quarantine";

    /// <summary>隔离区文件命名格式：{0}=SHA256前16位 {1}=原始文件名 {2}=Unix秒</summary>
    internal const string QuarantineFileNameFormat = "{0}_{1}_{2}.quarantine";

    /// <summary>隔离区文件扩展名</summary>
    internal const string QuarantineExtension = ".quarantine";

    /// <summary>隔离区临时文件扩展名（跨卷复制中间状态，非最终文件）</summary>
    internal const string CleaningTmpExtension = ".cleaning.tmp";

    /// <summary>大文件绕过隔离区阈值：隔离区卷剩余空间的 20%（与 B4 一致）</summary>
    internal const double LargeFileBypassRatio = 0.20;

    /// <summary>SHA-256 前缀截取长度（与命名格式中的 {0} 参数一致）</summary>
    internal const int Sha256PrefixLength = 16;

    /// <summary>进度报告最小间隔（毫秒），避免 UI 刷新过频</summary>
    private const int ProgressReportIntervalMs = 100;

    public QuarantineManager(
        IConfigService configService,
        ILogger<QuarantineManager> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ================================================================
    // IQuarantineManager 实现 — 目录解析
    // ================================================================

    /// <inheritdoc/>
    public Task<string?> GetQuarantineDirectoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogMethodEntry();

        try
        {
            var config = _configService.Current;

            // 隔离区容量为 0 表示已禁用
            if (config.QuarantineMaxSizeBytes <= 0)
            {
                _logger.LogDebug("隔离区已禁用（QuarantineMaxSizeBytes={MaxBytes}）",
                    config.QuarantineMaxSizeBytes);
                return Task.FromResult<string?>(null);
            }

            // 解析基路径：配置指定路径 > 默认 %LocalAppData%\SpaceClear
            var basePath = string.IsNullOrEmpty(config.DataStoragePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SpaceClear")
                : config.DataStoragePath;

            var quarantineDir = Path.Combine(basePath, QuarantineDirName);

            // 统一转换为 \\?\ 前缀格式
            var normalizedDir = PathNormalizer.Normalize(quarantineDir);

            // 目录不存在时自动创建（幂等安全）
            if (!Directory.Exists(normalizedDir))
            {
                Directory.CreateDirectory(normalizedDir);
                _logger.LogInformation("已创建隔离区目录: {Dir}", normalizedDir);
            }

            return Task.FromResult<string?>(normalizedDir);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取隔离区目录路径异常");
            return Task.FromResult<string?>(null);
        }
    }

    // ================================================================
    // IQuarantineManager 实现 — 统计与容量
    // ================================================================

    /// <inheritdoc/>
    public async Task<QuarantineStats> GetQuarantineStatsAsync(CancellationToken ct = default)
    {
        _logger.LogMethodEntry();

        try
        {
            var config = _configService.Current;

            // 隔离区禁用时直接返回空统计
            if (config.QuarantineMaxSizeBytes <= 0)
            {
                return new QuarantineStats
                {
                    QuarantineMaxSizeBytes = 0
                };
            }

            var quarantineDir = await GetQuarantineDirectoryAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(quarantineDir) || !Directory.Exists(quarantineDir))
            {
                return new QuarantineStats
                {
                    QuarantineMaxSizeBytes = config.QuarantineMaxSizeBytes
                };
            }

            // 统计变量初始化
            long totalSize = 0;
            var fileCount = 0;
            var expiredCount = 0;
            DateTime? oldestTimestamp = null;
            var retentionThreshold = DateTime.UtcNow.AddDays(-config.QuarantineRetentionDays);

            // 枚举隔离区中所有 .quarantine 文件
            try
            {
                foreach (var file in Directory.EnumerateFiles(quarantineDir, "*" + QuarantineExtension))
                {
                    // 每个文件检查取消令牌（大量文件时可及时响应取消）
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                        fileCount++;

                        // 追踪最旧文件的时间戳
                        if (oldestTimestamp == null || fileInfo.LastWriteTimeUtc < oldestTimestamp.Value)
                        {
                            oldestTimestamp = fileInfo.LastWriteTimeUtc;
                        }

                        // 解析文件名中的 Unix 时间戳判断是否过期
                        var fileName = Path.GetFileName(file);
                        var parsed = ParseQuarantineFileName(fileName);
                        if (parsed != null)
                        {
                            // 将 Unix 秒转换为 UTC DateTime 与保留阈值比较
                            var fileTime = DateTimeOffset.FromUnixTimeSeconds(parsed.Value.UnixSeconds).UtcDateTime;
                            if (fileTime < retentionThreshold)
                            {
                                expiredCount++;
                            }
                        }
                        // 无法解析的 .quarantine 文件不计入过期（安全保守策略）
                    }
                    catch (Exception ex)
                    {
                        // 单个文件的统计异常不中断整体统计
                        _logger.LogWarning(ex, "统计隔离区文件时跳过异常文件: {File}", file);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 枚举目录异常（如权限不足、目录被删除）不中断，返回已统计的部分数据
                _logger.LogWarning(ex, "枚举隔离区文件时发生异常，统计可能不完整");
            }

            // 查询所在卷的剩余空间（用于大文件绕过判断的卷级别参考）
            var driveFreeBytes = 0L;
            try
            {
                var denormalizedDir = PathNormalizer.Denormalize(quarantineDir);
                var driveRoot = Path.GetPathRoot(denormalizedDir);
                if (!string.IsNullOrEmpty(driveRoot))
                {
                    var driveInfo = new DriveInfo(driveRoot);
                    driveFreeBytes = driveInfo.AvailableFreeSpace;
                }
            }
            catch (Exception ex)
            {
                // 卷信息查询失败不影响主流程（绕过判断由 IsLargeFileForQuarantineAsync 单独处理）
                _logger.LogDebug(ex, "查询隔离区所在卷剩余空间失败");
            }

            // 计算容量指标
            var maxBytes = config.QuarantineMaxSizeBytes;
            var availableBytes = Math.Max(0, maxBytes - totalSize);
            var utilizationPct = maxBytes > 0
                ? Math.Min(100.0, (double)totalSize / maxBytes * 100.0)
                : 0.0;

            var stats = new QuarantineStats
            {
                TotalFileCount = fileCount,
                TotalSizeBytes = totalSize,
                QuarantineMaxSizeBytes = maxBytes,
                AvailableBytes = availableBytes,
                UtilizationPercentage = utilizationPct,
                ExpiredFileCount = expiredCount,
                OldestFileTimestamp = oldestTimestamp,
                DriveFreeBytes = driveFreeBytes
            };

            _logger.LogDebug("隔离区统计: {Stats}", stats);
            return stats;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("隔离区统计查询被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取隔离区统计信息异常");
            // 异常时返回空统计（保守策略：宁可显示无空间也不盲目允许写入）
            return new QuarantineStats
            {
                QuarantineMaxSizeBytes = _configService.Current.QuarantineMaxSizeBytes
            };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CanAccommodateAsync(long fileSize, CancellationToken ct = default)
    {
        try
        {
            var config = _configService.Current;
            if (config.QuarantineMaxSizeBytes <= 0)
                return false;

            var stats = await GetQuarantineStatsAsync(ct).ConfigureAwait(false);
            return stats.TotalSizeBytes + fileSize <= stats.QuarantineMaxSizeBytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查隔离区容量异常，保守返回 false");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsLargeFileForQuarantineAsync(long fileSize, CancellationToken ct = default)
    {
        try
        {
            var quarantineDir = await GetQuarantineDirectoryAsync(ct).ConfigureAwait(false);

            // 隔离区禁用或不可用：保守认为所有文件都"过大"，走绕过路径
            if (string.IsNullOrEmpty(quarantineDir))
                return true;

            // 目录不存在时（即将自动创建）无法预判卷空间，保守不走绕过
            if (!Directory.Exists(quarantineDir))
                return false;

            // 获取隔离区所在卷的可用空间
            var denormalizedDir = PathNormalizer.Denormalize(quarantineDir);
            var driveRoot = Path.GetPathRoot(denormalizedDir);
            if (string.IsNullOrEmpty(driveRoot))
                return false;

            var driveInfo = new DriveInfo(driveRoot);
            var availableSpace = driveInfo.AvailableFreeSpace;

            // 阈值：卷剩余空间的 20%
            var threshold = (long)(availableSpace * LargeFileBypassRatio);

            // 文件大小超过阈值则绕过隔离区
            var isLarge = fileSize > threshold;
            _logger.LogDebug("大文件绕过判断: Size={Size}, Threshold={Threshold}, Bypass={Bypass}",
                fileSize, threshold, isLarge);
            return isLarge;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 无法获取磁盘信息时保守处理：不绕过隔离区
            _logger.LogWarning(ex, "大文件绕过判断异常，保守返回 false（不绕过）");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> EnforceCapacityAsync(long additionalBytes, CancellationToken ct = default)
    {
        try
        {
            var config = _configService.Current;

            // 隔离区禁用时不强制
            if (config.QuarantineMaxSizeBytes <= 0)
                return false;

            var stats = await GetQuarantineStatsAsync(ct).ConfigureAwait(false);

            // 计算：当前已用 + 新增 是否超过上限
            var wouldExceed = stats.TotalSizeBytes + additionalBytes > stats.QuarantineMaxSizeBytes;

            if (wouldExceed)
            {
                _logger.LogWarning(
                    "隔离区容量不足：当前已用 {UsedBytes} 字节 + 新增 {AdditionalBytes} 字节"
                    + " > 上限 {MaxBytes} 字节（超出 {ExcessBytes} 字节）",
                    stats.TotalSizeBytes, additionalBytes, stats.QuarantineMaxSizeBytes,
                    stats.TotalSizeBytes + additionalBytes - stats.QuarantineMaxSizeBytes);
            }

            return !wouldExceed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 容量检查异常时保守返回 false（不允许写入）
            _logger.LogError(ex, "隔离区容量强制检查异常，保守返回 false");
            return false;
        }
    }

    // ================================================================
    // IQuarantineManager 实现 — 清理操作
    // ================================================================

    /// <inheritdoc/>
    public async Task<QuarantineCleanupResult> CleanupExpiredAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogMethodEntry();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 获取隔离区目录
            var quarantineDir = await GetQuarantineDirectoryAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(quarantineDir) || !Directory.Exists(quarantineDir))
            {
                _logger.LogInformation("隔离区不可用，无需清理");
                return new QuarantineCleanupResult
                {
                    CleanedFileCount = 0,
                    FreedBytes = 0,
                    FailedFiles = Array.Empty<string>(),
                    Elapsed = TimeSpan.Zero
                };
            }

            // 计算保留截止时间戳（Unix 秒）
            // 使用文件名中的 Unix 时间戳而非文件系统时间戳，
            // 因为文件复制/移动可能改变 LastWriteTime
            var config = _configService.Current;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var retentionThreshold = now - (config.QuarantineRetentionDays * 86400L);

            _logger.LogInformation("开始过期清理: 保留天数={Days}, 截止时间戳={Threshold}",
                config.QuarantineRetentionDays, retentionThreshold);

            var cleanedCount = 0;
            var freedBytes = 0L;
            var failedFiles = new List<string>();
            var lastProgressTime = Stopwatch.GetTimestamp();
            var progressIntervalTicks = ProgressReportIntervalMs * Stopwatch.Frequency / 1000;

            try
            {
                // 枚举隔离区中所有 .quarantine 文件
                foreach (var file in Directory.EnumerateFiles(quarantineDir, "*" + QuarantineExtension))
                {
                    ct.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileName(file);

                    // 从文件名解析 Unix 时间戳，无法解析则跳过（安全保守策略）
                    var parsed = ParseQuarantineFileName(fileName);
                    if (parsed == null)
                        continue;

                    // 判断是否过期：文件名时间戳 < 截止时间戳
                    if (parsed.Value.UnixSeconds >= retentionThreshold)
                        continue;

                    // 删除过期文件
                    try
                    {
                        var fileSize = new FileInfo(file).Length;
                        File.Delete(file);
                        cleanedCount++;
                        freedBytes += fileSize;
                        _logger.LogDebug("已删除过期隔离区文件: {File}, 大小={Size}",
                            file, fileSize);
                    }
                    catch (Exception ex)
                    {
                        // 单个文件删除失败不终止批次
                        _logger.LogWarning(ex, "删除过期隔离区文件失败: {File}", file);
                        failedFiles.Add(file);
                    }

                    // 进度报告（限流：每 100ms 最多报告一次）
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks - lastProgressTime >= progressIntervalTicks)
                    {
                        lastProgressTime = nowTicks;
                        progress?.Report(new OperationProgress
                        {
                            Phase = OperationPhase.Deleting,
                            CurrentFilePath = file,
                            CompletedCount = cleanedCount,
                            TotalCount = -1, // 过期文件总数未知
                            ProcessedBytes = freedBytes
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消：已删除的文件不回滚（与 OperationExecutor 一致）
                _logger.LogWarning("过期隔离区清理被取消: 已清理 {CleanedCount} 个文件", cleanedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "枚举隔离区文件异常，已清理 {CleanedCount} 个文件", cleanedCount);
            }

            stopwatch.Stop();
            _logger.LogMethodExit(
                $"过期清理完成: {cleanedCount}, 释放={freedBytes}, 失败={failedFiles.Count}, "
                + $"耗时={stopwatch.Elapsed.TotalSeconds:F2}s");

            return new QuarantineCleanupResult
            {
                CleanedFileCount = cleanedCount,
                FreedBytes = freedBytes,
                FailedFiles = failedFiles.AsReadOnly(),
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            // 取消操作是预期行为，返回已完成部分的清理结果
            _logger.LogWarning("过期隔离区清理被取消");
            stopwatch.Stop();
            return new QuarantineCleanupResult
            {
                CleanedFileCount = 0,
                FreedBytes = 0,
                FailedFiles = Array.Empty<string>(),
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "过期清理操作异常");
            return new QuarantineCleanupResult
            {
                CleanedFileCount = 0,
                FreedBytes = 0,
                FailedFiles = Array.Empty<string>(),
                Elapsed = TimeSpan.Zero
            };
        }
    }

    /// <inheritdoc/>
    public async Task<QuarantineCleanupResult> PurgeAllAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogMethodEntry();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 获取隔离区目录
            var quarantineDir = await GetQuarantineDirectoryAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(quarantineDir) || !Directory.Exists(quarantineDir))
            {
                _logger.LogInformation("隔离区不可用，无需清空");
                return new QuarantineCleanupResult
                {
                    CleanedFileCount = 0,
                    FreedBytes = 0,
                    FailedFiles = Array.Empty<string>(),
                    Elapsed = TimeSpan.Zero
                };
            }

            _logger.LogInformation("开始全量清空隔离区: {Dir}", quarantineDir);

            var cleanedCount = 0;
            var freedBytes = 0L;
            var failedFiles = new List<string>();
            var lastProgressTime = Stopwatch.GetTimestamp();
            var progressIntervalTicks = ProgressReportIntervalMs * Stopwatch.Frequency / 1000;

            try
            {
                // 枚举并删除所有 .quarantine 文件（不过滤非 .quarantine 文件）
                foreach (var file in Directory.EnumerateFiles(quarantineDir, "*" + QuarantineExtension))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fileSize = new FileInfo(file).Length;
                        File.Delete(file);
                        cleanedCount++;
                        freedBytes += fileSize;
                    }
                    catch (Exception ex)
                    {
                        // 单个文件删除失败不终止批次
                        _logger.LogWarning(ex, "清空隔离区时删除文件失败: {File}", file);
                        failedFiles.Add(file);
                    }

                    // 进度报告（限流：每 100ms 最多报告一次）
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks - lastProgressTime >= progressIntervalTicks)
                    {
                        lastProgressTime = nowTicks;
                        progress?.Report(new OperationProgress
                        {
                            Phase = OperationPhase.Deleting,
                            CurrentFilePath = file,
                            CompletedCount = cleanedCount,
                            TotalCount = -1, // 文件总数未知
                            ProcessedBytes = freedBytes
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消：已删除的文件不回滚
                _logger.LogWarning("隔离区清空被取消: 已清理 {CleanedCount} 个文件", cleanedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "枚举隔离区文件异常，已清理 {CleanedCount} 个文件", cleanedCount);
            }

            stopwatch.Stop();
            _logger.LogMethodExit(
                $"全量清空完成: {cleanedCount}, 释放={freedBytes}, 失败={failedFiles.Count}, "
                + $"耗时={stopwatch.Elapsed.TotalSeconds:F2}s");

            return new QuarantineCleanupResult
            {
                CleanedFileCount = cleanedCount,
                FreedBytes = freedBytes,
                FailedFiles = failedFiles.AsReadOnly(),
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            // 取消操作是预期行为，返回已完成部分的清理结果
            _logger.LogWarning("隔离区全量清空被取消");
            stopwatch.Stop();
            return new QuarantineCleanupResult
            {
                CleanedFileCount = 0,
                FreedBytes = 0,
                FailedFiles = Array.Empty<string>(),
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量清空操作异常");
            return new QuarantineCleanupResult
            {
                CleanedFileCount = 0,
                FreedBytes = 0,
                FailedFiles = Array.Empty<string>(),
                Elapsed = TimeSpan.Zero
            };
        }
    }

    // ================================================================
    // IQuarantineManager 实现 — 文件列表枚举（C7 隔离区管理界面）
    // ================================================================

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuarantineFileEntry>> ListQuarantineFilesAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogMethodEntry();

        try
        {
            // 获取隔离区目录路径
            var quarantineDir = await GetQuarantineDirectoryAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(quarantineDir) || !Directory.Exists(quarantineDir))
            {
                _logger.LogInformation("隔离区不可用，返回空文件列表");
                return Array.Empty<QuarantineFileEntry>();
            }

            var config = _configService.Current;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var retentionThreshold = now - (config.QuarantineRetentionDays * 86400L);
            var entries = new List<QuarantineFileEntry>();
            var lastProgressTime = Stopwatch.GetTimestamp();
            var progressIntervalTicks = ProgressReportIntervalMs * Stopwatch.Frequency / 1000;
            var processedCount = 0;

            // 枚举隔离区中所有 .quarantine 文件（排除 .cleaning.tmp 临时文件）
            try
            {
                foreach (var file in Directory.EnumerateFiles(quarantineDir, "*" + QuarantineExtension))
                {
                    // 每个文件检查取消令牌以支持及时取消
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var fileName = Path.GetFileName(file);

                        // 从文件名解析元数据
                        var parsed = ParseQuarantineFileName(fileName);
                        if (parsed == null)
                        {
                            // 无法解析的文件名跳过但不中断整体枚举
                            _logger.LogDebug("跳过无法解析的隔离区文件: {File}", file);
                            continue;
                        }

                        // 判断是否过期：文件名时间戳 < 截止时间戳
                        var isExpired = parsed.Value.UnixSeconds < retentionThreshold;
                        var quarantineTimestamp = DateTimeOffset
                            .FromUnixTimeSeconds(parsed.Value.UnixSeconds).UtcDateTime;

                        entries.Add(new QuarantineFileEntry
                        {
                            FullPath = file,
                            OriginalFileName = parsed.Value.OriginalFileName,
                            Sha256Prefix = parsed.Value.Sha256Prefix,
                            QuarantineTimestamp = quarantineTimestamp,
                            FileSizeBytes = fileInfo.Length,
                            IsExpired = isExpired,
                            FileSizeText = FormatBytesForDisplay(fileInfo.Length)
                        });

                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        // 单个文件的处理异常不中断整体枚举
                        _logger.LogWarning(ex, "处理隔离区文件时跳过异常文件: {File}", file);
                    }

                    // 进度报告（限流：每 100ms 最多报告一次）
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks - lastProgressTime >= progressIntervalTicks)
                    {
                        lastProgressTime = nowTicks;
                        progress?.Report(new OperationProgress
                        {
                            Phase = OperationPhase.Hashing,
                            CurrentFilePath = file,
                            CompletedCount = processedCount,
                            TotalCount = -1, // 文件总数未知，枚举过程中动态增长
                            ProcessedBytes = 0
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 枚举目录异常（如权限不足、目录被删除）不中断，返回已处理的部分数据
                _logger.LogWarning(ex, "枚举隔离区文件时发生异常，已处理 {Count} 个文件", processedCount);
            }

            // 按隔离时间降序排列（最新的在前）
            entries.Sort((a, b) => b.QuarantineTimestamp.CompareTo(a.QuarantineTimestamp));

            _logger.LogInformation("隔离区文件列表枚举完成: 共 {Count} 个文件", entries.Count);
            return entries.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("隔离区文件列表枚举被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取隔离区文件列表异常");
            return Array.Empty<QuarantineFileEntry>();
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteQuarantineFileAsync(
        string quarantineFilePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // 参数校验：路径不能为空
            if (string.IsNullOrEmpty(quarantineFilePath))
            {
                _logger.LogWarning("尝试删除隔离区文件但路径为空");
                return Task.FromResult(false);
            }

            // 文件不存在时视为成功（幂等语义）
            if (!File.Exists(quarantineFilePath))
            {
                _logger.LogDebug("隔离区文件不存在，幂等返回成功: {File}", quarantineFilePath);
                return Task.FromResult(true);
            }

            // 删除文件（系统调用，不触发墓碑清理或审计日志）
            File.Delete(quarantineFilePath);
            _logger.LogDebug("已删除隔离区文件: {File}", quarantineFilePath);
            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 删除失败（权限不足、文件锁定等）返回 false
            _logger.LogWarning(ex, "删除隔离区文件失败: {File}", quarantineFilePath);
            return Task.FromResult(false);
        }
    }

    // ================================================================
    // IQuarantineManager 实现 — 文件命名
    // ================================================================

    /// <summary>
    /// 格式化字节数为人类可读的字符串，用于 UI 展示。
    /// 与 MainViewModel.FormatBytesForDisplay 保持一致。
    /// </summary>
    private static string FormatBytesForDisplay(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F2} KB",
            _ => $"{bytes} B"
        };
    }

    /// <inheritdoc/>
    public string ConstructQuarantineFileName(string sha256Hash, string originalFileName)
    {
        // 参数校验
        if (string.IsNullOrEmpty(sha256Hash))
            throw new ArgumentException("SHA-256 哈希不能为空", nameof(sha256Hash));
        if (string.IsNullOrEmpty(originalFileName))
            throw new ArgumentException("原始文件名不能为空", nameof(originalFileName));

        // 截取 SHA-256 前 16 位作为前缀
        var sha256Prefix = sha256Hash.Length >= Sha256PrefixLength
            ? sha256Hash[..Sha256PrefixLength]
            : sha256Hash;

        // 当前 UTC Unix 秒时间戳
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 格式: {SHA256前16位}_{原始文件名}_{Unix秒}.quarantine
        var fileName = string.Format(
            CultureInfo.InvariantCulture,
            QuarantineFileNameFormat,
            sha256Prefix, originalFileName, unixSeconds);

        return fileName;
    }

    /// <inheritdoc/>
    public (string Sha256Prefix, string OriginalFileName, long UnixSeconds)? ParseQuarantineFileName(
        string fileName)
    {
        // 参数校验
        if (string.IsNullOrEmpty(fileName))
            return null;

        // 必须以 .quarantine 结尾
        if (!fileName.EndsWith(QuarantineExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        // 去除 .quarantine 扩展名
        var nameWithoutExt = fileName[..^QuarantineExtension.Length];

        // 解析策略：
        // 格式为 {SHA256}_{OriginalFileName}_{UnixSeconds}
        // SHA256 固定 16 位十六进制（不含下划线）
        // OriginalFileName 可能包含下划线（如 项目_文档_v2.docx）
        // UnixSeconds 为纯数字（不含下划线）
        //
        // 因此：第一个 _ 之前是 SHA256 前缀，最后一个 _ 之后是 Unix 时间戳，
        // 中间部分（含所有 _）为原始文件名

        var firstUnderscore = nameWithoutExt.IndexOf('_');
        if (firstUnderscore <= 0)
            return null; // 必须先有 SHA256 前缀

        var lastUnderscore = nameWithoutExt.LastIndexOf('_');
        if (lastUnderscore <= firstUnderscore)
            return null; // 必须有三部分

        var sha256Prefix = nameWithoutExt[..firstUnderscore];
        var originalFileName = nameWithoutExt[(firstUnderscore + 1)..lastUnderscore];
        var unixSecondsStr = nameWithoutExt[(lastUnderscore + 1)..];

        // 验证 Unix 时间戳是否为有效的纯数字
        if (!long.TryParse(unixSecondsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return null;

        // 验证合理范围：2020-01-01 ~ 2100-01-01
        if (unixSeconds < 1577836800 || unixSeconds > 4102444800)
            return null;

        return (sha256Prefix, originalFileName, unixSeconds);
    }
}
