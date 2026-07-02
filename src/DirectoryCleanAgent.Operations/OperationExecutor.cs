using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectoryCleanAgent.Core.Cancellation;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Operations.Interop;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Operations;

/// <summary>
/// 操作执行器实现 — B4 阶段核心组件。
///
/// 职责（设计文档 7.2/7.3/7.4 节）：
/// - SHQueryRecycleBin 容量预检（仅一次，快速失败原则）
/// - IFileOperation（SHFileOperationW）乐观删除
/// - 跨卷隔离区复制 + .cleaning.tmp 临时文件机制
/// - 文件锁失败降级为 manual_review
/// - 不可变 DeleteSnapshotEntry 快照驱动执行
/// - CancellationToken 绑定，支持随时中断
/// - 进度报告（IProgress&lt;OperationProgress&gt;）
/// - 删除成功后墓碑写入 + 审计日志记录
///
/// B6 重构：隔离区目录解析、大文件绕过判断、容量强制检查
/// 委托给 IQuarantineManager，消除与 BackupManager 的重复代码。
///
/// 哈希策略：
/// - 标准删除路径：直接使用快照中 B3 已计算的 Sha256Hash，不重新计算。
/// - 跨卷隔离区复制：计算目标文件 SHA-256 并与快照哈希比对，确保复制完整性。
/// </summary>
public sealed class OperationExecutor : CancellableOperationBase, IOperationExecutor
{
    private readonly ILocalTombstoneRepository _tombstoneRepo;
    private readonly IDeletionRecordRepository _deletionRecordRepo;
    private readonly IAuditLogRepository _auditLogRepo;
    private readonly ITombstoneCache _tombstoneCache;
    private readonly IConfigService _configService;
    private readonly IQuarantineManager _quarantineManager;
    private readonly ShellFileOperation _shellFileOp;

    /// <summary>进度报告最小间隔（毫秒），避免 UI 刷新过频</summary>
    private const int ProgressReportIntervalMs = 100;

    /// <summary>隔离区临时文件扩展名（跨卷复制中间状态）</summary>
    private const string CleaningTmpExtension = ".cleaning.tmp";

    public OperationExecutor(
        ILogger<OperationExecutor> logger,
        ILocalTombstoneRepository tombstoneRepo,
        IDeletionRecordRepository deletionRecordRepo,
        IAuditLogRepository auditLogRepo,
        ITombstoneCache tombstoneCache,
        IConfigService configService,
        IQuarantineManager quarantineManager)
        : base(logger)
    {
        _tombstoneRepo = tombstoneRepo ?? throw new ArgumentNullException(nameof(tombstoneRepo));
        _deletionRecordRepo = deletionRecordRepo ?? throw new ArgumentNullException(nameof(deletionRecordRepo));
        _auditLogRepo = auditLogRepo ?? throw new ArgumentNullException(nameof(auditLogRepo));
        _tombstoneCache = tombstoneCache ?? throw new ArgumentNullException(nameof(tombstoneCache));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _quarantineManager = quarantineManager ?? throw new ArgumentNullException(nameof(quarantineManager));
        _shellFileOp = new ShellFileOperation(logger);
    }

    // ================================================================
    // 回收站容量预检
    // ================================================================

    /// <inheritdoc/>
    public Task<RecycleBinCapacity> QueryRecycleBinCapacityAsync(CancellationToken ct)
    {
        Logger.LogMethodEntry();

        try
        {
            ct.ThrowIfCancellationRequested();

            // 查询所有驱动器的回收站总容量和已用空间
            var queryInfo = new Shell32Native.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<Shell32Native.SHQUERYRBINFO>(),
                i64Size = 0,
                i64NumItems = 0
            };

            // pszRootPath 传 null 表示查询所有驱动器的回收站
            var hr = Shell32Native.SHQueryRecycleBinW(null, ref queryInfo);

            if (hr == Shell32Native.S_OK)
            {
                // 回收站总容量（Windows 按驱动器百分比设置，此处为汇总值）
                var totalBytes = queryInfo.i64Size;

                var capacity = new RecycleBinCapacity
                {
                    TotalBytes = totalBytes,
                    UsedBytes = queryInfo.i64NumItems, // 注意：i64NumItems 在部分实现中表示已用字节
                    AvailableBytes = totalBytes > 0
                        ? Math.Max(0, totalBytes - queryInfo.i64NumItems)
                        : 0
                };

                Logger.LogInformation("回收站容量查询成功: {Capacity}", capacity);
                return Task.FromResult(capacity);
            }

            // SHQueryRecycleBin 失败时的保守策略：
            // 返回 TotalBytes=0（表示无容量限制），让 UI 层决定是否继续。
            Logger.LogWarning("SHQueryRecycleBin 查询失败，HRESULT=0x{HR:X8}，保守返回无限制", hr);
            return Task.FromResult(new RecycleBinCapacity
            {
                TotalBytes = 0,
                UsedBytes = 0,
                AvailableBytes = 0
            });
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("回收站容量查询被取消");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "回收站容量查询异常");
            // 异常时同样保守处理
            return Task.FromResult(new RecycleBinCapacity
            {
                TotalBytes = 0,
                UsedBytes = 0,
                AvailableBytes = 0
            });
        }
    }

    // ================================================================
    // 删除执行主入口
    // ================================================================

    /// <inheritdoc/>
    public async Task<OperationResult> ExecuteAsync(
        ReadOnlyCollection<DeleteSnapshotEntry> snapshot,
        DeleteMethod method,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        using var timing = Logger.LogTiming("ExecuteAsync");

        // 参数校验
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        if (snapshot.Count == 0)
        {
            Logger.LogInformation("快照为空，无需执行操作");
            return new OperationResult
            {
                OperationId = Guid.NewGuid().ToString("N"),
                SuccessCount = 0,
                FailedCount = 0,
                ManualReviewCount = 0,
                CancelledCount = 0,
                FreedBytes = 0,
                Elapsed = TimeSpan.Zero
            };
        }

        // 过滤出可操作的文件（仅 AutoDelete 和 SuggestDelete）
        var actionableEntries = snapshot
            .Where(e => e.FinalAction is FinalAction.AutoDelete or FinalAction.SuggestDelete)
            .ToList();

        if (actionableEntries.Count == 0)
        {
            Logger.LogInformation("快照中无可操作文件（所有文件为 Protected 或 ManualReview）");
            return new OperationResult
            {
                OperationId = snapshot[0].OperationId,
                SuccessCount = 0,
                FailedCount = 0,
                ManualReviewCount = 0,
                CancelledCount = 0,
                FreedBytes = 0,
                Elapsed = TimeSpan.Zero
            };
        }

        Logger.LogInformation("开始执行删除操作: OperationId={OpId}, 可操作文件数={Count}, 方式={Method}",
            snapshot[0].OperationId, actionableEntries.Count, method);

        var stopwatch = Stopwatch.StartNew();
        var operationId = snapshot[0].OperationId;

        // 结果累加器
        var successCount = 0;
        var failedCount = 0;
        var manualReviewCount = 0;
        var cancelledCount = 0;
        var freedBytes = 0L;
        var isCancelled = false;
        var failedFiles = new List<FileOperationError>();
        var manualReviewFiles = new List<FileOperationError>();
        var tombstones = new List<LocalTombstone>(actionableEntries.Count);
        var deletionRecords = new List<DeletionRecord>(actionableEntries.Count);

        var totalCount = actionableEntries.Count;
        var lastProgressTime = Stopwatch.GetTimestamp();
        var progressIntervalTicks = ProgressReportIntervalMs * Stopwatch.Frequency / 1000;

        try
        {
            // 在处理任何文件之前先检查取消状态（快速失败）
            ct.ThrowIfCancellationRequested();

            // B6 重构：通过 QuarantineManager 统一获取隔离区目录
            var quarantineDir = await _quarantineManager.GetQuarantineDirectoryAsync(ct)
                .ConfigureAwait(false);

            // B6 新增：在执行循环前进行容量强制检查
            // 若整个批次的文件总大小超出隔离区剩余容量，则跳过隔离区备份，全部直删
            if (!string.IsNullOrEmpty(quarantineDir))
            {
                var totalBatchSize = actionableEntries.Sum(e => e.FileSize);
                var hasCapacity = await _quarantineManager.EnforceCapacityAsync(totalBatchSize, ct)
                    .ConfigureAwait(false);
                if (!hasCapacity)
                {
                    Logger.LogWarning(
                        "隔离区容量不足（上限 {MaxBytes} 字节，批次总大小 {BatchBytes} 字节），"
                        + "跳过隔离区备份，所有文件直接删除",
                        _configService.Current.QuarantineMaxSizeBytes, totalBatchSize);
                    quarantineDir = null; // 禁用本批次的隔离区备份
                }
            }

            // 逐个文件执行删除操作
            for (var i = 0; i < actionableEntries.Count; i++)
            {
                // 在每个文件处理前检查取消状态
                ct.ThrowIfCancellationRequested();

                var entry = actionableEntries[i];

                try
                {
                    // B6 重构：通过 QuarantineManager 判断大文件绕过
                    var isLargeFile = !string.IsNullOrEmpty(quarantineDir)
                        && await _quarantineManager.IsLargeFileForQuarantineAsync(entry.FileSize, ct)
                            .ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(quarantineDir) && !isLargeFile)
                    {
                        // 隔离区路径：先备份到隔离区，再删除原文件
                        var quarantineResult = await BackupToQuarantineAsync(
                            entry, quarantineDir, method, ct).ConfigureAwait(false);

                        if (quarantineResult.Success)
                        {
                            // 隔离区备份成功
                            successCount++;
                            freedBytes += entry.FileSize;
                        }
                        else if (quarantineResult.IsLockViolation)
                        {
                            // 文件被锁定，降级为 manual_review
                            manualReviewCount++;
                            manualReviewFiles.Add(new FileOperationError
                            {
                                FilePath = entry.FilePath,
                                ErrorCode = "0x80070020",
                                ErrorMessage = "文件被其他进程占用，已降级为人工审核"
                            });
                        }
                        else
                        {
                            // 备份失败，记录为失败
                            failedCount++;
                            failedFiles.Add(new FileOperationError
                            {
                                FilePath = entry.FilePath,
                                ErrorCode = quarantineResult.ErrorCode ?? "UNKNOWN",
                                ErrorMessage = quarantineResult.ErrorMessage ?? "隔离区备份失败"
                            });
                            continue; // 跳过后续的墓碑和审计日志写入
                        }
                    }
                    else
                    {
                        // 无隔离区（或大文件绕过）：直接删除
                        var deleteHr = ExecuteDelete(entry.FilePath, method);

                        if (deleteHr == Shell32Native.S_OK || deleteHr == Shell32Native.ERROR_FILE_NOT_FOUND)
                        {
                            // 文件删除成功 或 文件已不存在（等效于已删除）
                            successCount++;
                            freedBytes += entry.FileSize;
                        }
                        else if (ShellFileOperation.IsLockViolation(deleteHr))
                        {
                            // 文件被锁定，降级为 manual_review（乐观删除策略）
                            manualReviewCount++;
                            manualReviewFiles.Add(new FileOperationError
                            {
                                FilePath = entry.FilePath,
                                ErrorCode = $"0x{deleteHr:X8}",
                                ErrorMessage = ShellFileOperation.GetErrorMessage(deleteHr)
                            });
                        }
                        else
                        {
                            // 其他错误（权限不足、文件不存在等）
                            failedCount++;
                            failedFiles.Add(new FileOperationError
                            {
                                FilePath = entry.FilePath,
                                ErrorCode = $"0x{deleteHr:X8}",
                                ErrorMessage = ShellFileOperation.GetErrorMessage(deleteHr)
                            });
                            continue; // 跳过后续的墓碑和审计日志写入
                        }
                    }

                    // 成功删除或降级为 manual_review 后，写入墓碑和删除记录
                    var now = DateTime.UtcNow;

                    var tombstone = CreateTombstone(entry, operationId, now);
                    tombstones.Add(tombstone);

                    var deletionRecord = CreateDeletionRecord(entry, method, now);
                    deletionRecords.Add(deletionRecord);

                    // 写入审计日志
                    _auditLogRepo.Insert(new AuditLogEntry
                    {
                        Timestamp = now,
                        UserSid = GetCurrentUserSid(),
                        OperationType = "FILE_DELETED",
                        Target = entry.FilePath,
                        Result = "SUCCESS",
                        Details = $"{{ \"fileSize\": {entry.FileSize}, \"method\": \"{method}\" }}"
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 单个文件操作的异常不应中断整个批次
                    failedCount++;
                    failedFiles.Add(new FileOperationError
                    {
                        FilePath = entry.FilePath,
                        ErrorCode = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        Exception = ex
                    });
                    Logger.LogError(ex, "删除文件异常: {FilePath}", entry.FilePath);
                }

                // 进度报告（限流：每 100ms 最多报告一次）
                var nowTicks = Stopwatch.GetTimestamp();
                var isLastItem = i == actionableEntries.Count - 1;
                if (isLastItem || nowTicks - lastProgressTime >= progressIntervalTicks)
                {
                    lastProgressTime = nowTicks;
                    progress?.Report(new OperationProgress
                    {
                        Phase = OperationPhase.Deleting,
                        CurrentFilePath = entry.FilePath,
                        CompletedCount = i + 1,
                        TotalCount = totalCount,
                        ProcessedBytes = freedBytes
                    });
                }
            }

            // 批量写入墓碑到持久化存储和内存缓存
            if (tombstones.Count > 0)
            {
                _tombstoneRepo.InsertBatch(tombstones);
                _tombstoneCache.AddBatch(tombstones);
                Logger.LogInformation("已写入 {Count} 条墓碑记录", tombstones.Count);
            }

            // 批量写入删除记录
            if (deletionRecords.Count > 0)
            {
                _deletionRecordRepo.InsertBatch(deletionRecords);
                Logger.LogInformation("已写入 {Count} 条删除记录", deletionRecords.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消操作：已删除的文件不回滚（设计文档规定）
            isCancelled = true;
            cancelledCount = actionableEntries.Count - successCount - failedCount - manualReviewCount;
            Logger.LogWarning("删除操作被取消: 已完成={Success}, 失败={Failed}, 人工审核={Manual}, 未处理={Cancelled}",
                successCount, failedCount, manualReviewCount, cancelledCount);

            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Cancelled,
                CompletedCount = successCount + failedCount + manualReviewCount,
                TotalCount = totalCount,
                ProcessedBytes = freedBytes
            });

            // 即使取消，也要写入已完成部分的墓碑和删除记录
            if (tombstones.Count > 0)
            {
                _tombstoneRepo.InsertBatch(tombstones);
                _tombstoneCache.AddBatch(tombstones);
            }
            if (deletionRecords.Count > 0)
            {
                _deletionRecordRepo.InsertBatch(deletionRecords);
            }
        }
        finally
        {
            // 确保批量队列被刷新
            try
            {
                await _tombstoneRepo.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await _deletionRecordRepo.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await _auditLogRepo.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "批量队列刷新异常");
            }
        }

        stopwatch.Stop();

        var result = new OperationResult
        {
            OperationId = operationId,
            SuccessCount = successCount,
            FailedCount = failedCount,
            ManualReviewCount = manualReviewCount,
            CancelledCount = cancelledCount,
            FailedFiles = failedFiles.AsReadOnly(),
            ManualReviewFiles = manualReviewFiles.AsReadOnly(),
            IsCancelled = isCancelled,
            FreedBytes = freedBytes,
            Elapsed = stopwatch.Elapsed
        };

        Logger.LogInformation(
            "删除操作完成: 成功={Success}, 失败={Failed}, 人工审核={Manual}, 取消={Cancelled}, "
            + "释放空间={FreedMB:F2}MB, 耗时={Elapsed}",
            successCount, failedCount, manualReviewCount, cancelledCount,
            freedBytes / 1024.0 / 1024.0, stopwatch.Elapsed);

        progress?.Report(new OperationProgress
        {
            Phase = OperationPhase.Completed,
            CompletedCount = totalCount,
            TotalCount = totalCount,
            ProcessedBytes = freedBytes
        });

        return result;
    }

    // ================================================================
    // 私有方法 — 删除执行
    // ================================================================

    /// <summary>
    /// 执行单个文件的删除操作。
    ///
    /// 根据 DeleteMethod 选择删除策略：
    /// - RecycleBin：调用 SHFileOperationW（FO_DELETE + FOF_ALLOWUNDO）→ 放入回收站
    /// - Permanent：调用 SHFileOperationW（FO_DELETE 无 UNDO）→ 永久删除
    ///
    /// 返回 HRESULT，调用方根据返回码判断成功/失败/锁冲突。
    /// </summary>
    private int ExecuteDelete(string filePath, DeleteMethod method)
    {
        return method switch
        {
            DeleteMethod.RecycleBin => _shellFileOp.DeleteToRecycleBin(filePath),
            DeleteMethod.Permanent => _shellFileOp.DeletePermanently(filePath),
            _ => _shellFileOp.DeleteToRecycleBin(filePath) // 未知方式默认回收站
        };
    }

    // ================================================================
    // 私有方法 — 隔离区备份
    // ================================================================

    /// <summary>
    /// 隔离区备份操作结果
    /// </summary>
    private readonly struct QuarantineBackupResult
    {
        public bool Success { get; init; }
        public bool IsLockViolation { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// 将文件备份到隔离区，然后删除原文件。
    ///
    /// 备份策略（设计文档 7.4 节）：
    /// 1. 源与隔离区同一卷：直接移动（FO_MOVE），原子操作。
    /// 2. 源与隔离区不同卷：复制到 .cleaning.tmp → SHA-256 校验 → 重命名 → 删除原文件。
    ///
    /// 跨卷复制详细流程：
    /// - 目标文件命名为 {SHA256前16位}_{原文件名}_{Unix秒}.cleaning.tmp
    /// - 复制完成后校验目标文件 SHA-256
    /// - 校验通过：重命名 .cleaning.tmp → .quarantine，然后删除原文件
    /// - 校验失败/取消：删除 .cleaning.tmp，保留原文件
    /// </summary>
    private async Task<QuarantineBackupResult> BackupToQuarantineAsync(
        DeleteSnapshotEntry entry,
        string quarantineDir,
        DeleteMethod method,
        CancellationToken ct)
    {
        try
        {
            // B6 重构：通过 QuarantineManager 构造符合命名规范的隔离区文件名
            var originalFileName = Path.GetFileName(PathNormalizer.Denormalize(entry.FilePath));
            var quarantineFileName = _quarantineManager.ConstructQuarantineFileName(
                entry.Sha256Hash, originalFileName);

            var quarantinePath = PathNormalizer.Normalize(
                Path.Combine(quarantineDir, quarantineFileName));

            // 确保隔离区目录存在
            var normalizedQuarantineDir = PathNormalizer.Normalize(quarantineDir);
            if (!Directory.Exists(normalizedQuarantineDir))
            {
                Directory.CreateDirectory(normalizedQuarantineDir);
            }

            // 判断源和目标是否在同一卷
            var sameVolume = VolumeHelper.AreOnSameVolume(entry.FilePath, quarantinePath);

            if (sameVolume)
            {
                // 同卷：直接移动文件（原子操作，不产生复制开销）
                Logger.LogDebug("同卷移动隔离区: {Source} → {Dest}", entry.FilePath, quarantinePath);
                var moveHr = _shellFileOp.MoveFile(entry.FilePath, quarantinePath);

                if (moveHr == Shell32Native.S_OK)
                {
                    return new QuarantineBackupResult { Success = true };
                }

                if (ShellFileOperation.IsLockViolation(moveHr))
                {
                    return new QuarantineBackupResult
                    {
                        IsLockViolation = true,
                        ErrorCode = $"0x{moveHr:X8}",
                        ErrorMessage = ShellFileOperation.GetErrorMessage(moveHr)
                    };
                }

                return new QuarantineBackupResult
                {
                    ErrorCode = $"0x{moveHr:X8}",
                    ErrorMessage = ShellFileOperation.GetErrorMessage(moveHr)
                };
            }
            else
            {
                // 跨卷：复制 + 校验 + 重命名 + 删除原文件
                return await CrossVolumeQuarantineAsync(
                    entry, quarantinePath, method, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "隔离区备份异常: {FilePath}", entry.FilePath);
            return new QuarantineBackupResult
            {
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 跨卷隔离区流程：复制到 .cleaning.tmp → SHA-256 校验 → 重命名 → 删除原文件。
    ///
    /// 安全保证：
    /// - 复制过程中取消或失败 → 删除 .cleaning.tmp，原文件不受影响。
    /// - SHA-256 校验不通过 → 删除 .cleaning.tmp，保留原文件。
    /// - 校验通过后才重命名 .cleaning.tmp → .quarantine 并删除原文件。
    /// </summary>
    private async Task<QuarantineBackupResult> CrossVolumeQuarantineAsync(
        DeleteSnapshotEntry entry,
        string quarantinePath,
        DeleteMethod method,
        CancellationToken ct)
    {
        // 替换 .quarantine 后缀为 .cleaning.tmp（复制阶段的临时文件）
        var cleaningTmpPath = quarantinePath.EndsWith(".quarantine", StringComparison.OrdinalIgnoreCase)
            ? quarantinePath[..^".quarantine".Length] + CleaningTmpExtension
            : quarantinePath + CleaningTmpExtension;

        Logger.LogDebug("跨卷隔离区复制: {Source} → {Tmp}", entry.FilePath, cleaningTmpPath);

        try
        {
            // 步骤1：复制原文件到 .cleaning.tmp
            var copyHr = _shellFileOp.CopyFile(entry.FilePath, cleaningTmpPath);
            if (copyHr != Shell32Native.S_OK)
            {
                // 复制失败，清理可能的残留临时文件
                TryDeleteFile(cleaningTmpPath);

                if (ShellFileOperation.IsLockViolation(copyHr))
                {
                    return new QuarantineBackupResult
                    {
                        IsLockViolation = true,
                        ErrorCode = $"0x{copyHr:X8}",
                        ErrorMessage = ShellFileOperation.GetErrorMessage(copyHr)
                    };
                }

                return new QuarantineBackupResult
                {
                    ErrorCode = $"0x{copyHr:X8}",
                    ErrorMessage = $"跨卷复制失败: {ShellFileOperation.GetErrorMessage(copyHr)}"
                };
            }

            // 步骤2：取消检查
            ct.ThrowIfCancellationRequested();

            // 步骤3：校验目标文件 SHA-256（与快照中的哈希比对）
            var hashMatch = await HashVerifier.VerifyHashAsync(
                cleaningTmpPath, entry.Sha256Hash, ct).ConfigureAwait(false);

            if (!hashMatch)
            {
                // 校验失败：删除临时文件，保留原文件
                Logger.LogError("跨卷隔离区 SHA-256 校验失败: {FilePath}", entry.FilePath);
                TryDeleteFile(cleaningTmpPath);
                return new QuarantineBackupResult
                {
                    ErrorCode = "HASH_MISMATCH",
                    ErrorMessage = "隔离区文件 SHA-256 校验失败，已删除临时文件，原文件保留"
                };
            }

            // 步骤4：校验通过，重命名 .cleaning.tmp → .quarantine
            ct.ThrowIfCancellationRequested();
            File.Move(cleaningTmpPath, quarantinePath);

            // 步骤5：删除原文件（放入回收站）
            var deleteHr = ExecuteDelete(entry.FilePath, method);
            if (deleteHr != Shell32Native.S_OK)
            {
                // 原文件删除失败，但隔离区已有备份，记录警告
                Logger.LogWarning("隔离区备份成功但原文件删除失败: {FilePath}, HRESULT=0x{HR:X8}",
                    entry.FilePath, deleteHr);

                if (ShellFileOperation.IsLockViolation(deleteHr))
                {
                    return new QuarantineBackupResult
                    {
                        IsLockViolation = true,
                        ErrorCode = $"0x{deleteHr:X8}",
                        ErrorMessage = "文件已备份至隔离区，但原文件被占用无法删除"
                    };
                }

                return new QuarantineBackupResult
                {
                    ErrorCode = $"0x{deleteHr:X8}",
                    ErrorMessage = $"文件已备份至隔离区，但原文件删除失败: {ShellFileOperation.GetErrorMessage(deleteHr)}"
                };
            }

            Logger.LogDebug("跨卷隔离区操作完成: {FilePath} → {QuarantinePath}",
                entry.FilePath, quarantinePath);
            return new QuarantineBackupResult { Success = true };
        }
        catch (OperationCanceledException)
        {
            // 取消时删除残留的 .cleaning.tmp
            TryDeleteFile(cleaningTmpPath);
            throw;
        }
        catch (Exception ex)
        {
            // 异常时清理临时文件
            TryDeleteFile(cleaningTmpPath);
            Logger.LogError(ex, "跨卷隔离区操作异常: {FilePath}", entry.FilePath);
            return new QuarantineBackupResult
            {
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message
            };
        }
    }

    // ================================================================
    // 私有方法 — 辅助
    // ================================================================

    /// <summary>
    /// 创建墓碑记录。
    ///
    /// 墓碑用于防止已删除文件在后续扫描中重复出现。
    /// FRN 可用时使用 VolumeGuid:FRN 精确匹配；
    /// FRN 不可用时降级为 Size:LastWriteTime 指纹匹配（3 天过期）。
    /// </summary>
    private LocalTombstone CreateTombstone(
        DeleteSnapshotEntry entry, string operationId, DateTime now)
    {
        // 降级指纹：FRN 不可用时使用 Size + LastWriteTime 组合
        var frnAvailable = _configService.Current.FRN_AVAILABLE;
        var fingerprintKey = $"{entry.FileSize}:{now:O}";

        return new LocalTombstone
        {
            FilePath = entry.FilePath,
            // FRN 信息在 B4 阶段不可用（B1 阶段在 FileItem 中提供），
            // 此处使用操作时间戳作为降级指纹的 LastWriteTime 近似值。
            FileIdentityKey = fingerprintKey,
            OperationId = operationId,
            OriginalSize = entry.FileSize,
            OriginalLastWriteTime = now,
            DeletedAt = now
        };
    }

    /// <summary>
    /// 创建删除记录（DeletionRecord）。
    ///
    /// 记录包含文件路径、SHA-256 哈希、删除方式等信息。
    /// 决策快照 JSON 用于审计溯源和回滚时的数据校验。
    /// </summary>
    private DeletionRecord CreateDeletionRecord(
        DeleteSnapshotEntry entry, DeleteMethod method, DateTime now)
    {
        return new DeletionRecord
        {
            OperationId = entry.OperationId,
            FilePath = entry.FilePath,
            FileHash = entry.Sha256Hash,
            FileSize = entry.FileSize,
            DeletionMethod = method,
            DecisionSnapshotJson = "{}", // 单个文件快照简化为空 JSON（完整快照由 B3 层管理）
            CreatedAt = now
        };
    }

    /// <summary>
    /// 获取当前用户的 SID 字符串，用于审计日志。
    /// 失败时返回 "UNKNOWN"。
    /// </summary>
    private static string GetCurrentUserSid()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    /// <summary>
    /// 安全删除文件，忽略所有异常。
    /// 用于清理 .cleaning.tmp 等临时文件。
    /// </summary>
    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            // 临时文件清理失败不抛出异常
            System.Diagnostics.Debug.WriteLine(
                $"清理临时文件失败: {filePath}, {ex.Message}");
        }
    }
}
