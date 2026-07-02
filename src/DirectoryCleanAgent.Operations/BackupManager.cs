using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
/// 回滚管理器实现 — B5 阶段核心组件。
///
/// 职责（设计文档 7.6 节）：
/// - 根据 operation_id 从历史记录中查找删除批次，从隔离区或回收站恢复文件。
/// - 恢复后进行 SHA-256 校验，哈希不匹配则拒绝恢复并警告用户。
/// - 恢复成功后，按 operation_id 批量删除 LocalTombstone 中所有关联记录。
/// - 支持回滚历史查询和独立哈希校验。
///
/// 隔离区恢复是主路径（我们控制文件命名规范），回收站恢复是次要路径。
///
/// B6 重构：隔离区目录解析和文件命名委托给 IQuarantineManager，
/// 消除与 OperationExecutor 的重复代码。
/// </summary>
public sealed class BackupManager : CancellableOperationBase, IBackupManager
{
    private readonly ILocalTombstoneRepository _tombstoneRepo;
    private readonly IDeletionRecordRepository _deletionRecordRepo;
    private readonly ITombstoneCache _tombstoneCache;
    private readonly IConfigService _configService;
    private readonly IQuarantineManager _quarantineManager;
    private readonly ShellFileOperation _shellFileOp;

    /// <summary>进度报告最小间隔（毫秒），避免 UI 刷新过频</summary>
    private const int ProgressReportIntervalMs = 100;

    /// <summary>恢复文件时路径冲突的重试上限</summary>
    private const int MaxConflictRetry = 99;

    /// <summary>路径冲突时的恢复文件名后缀格式</summary>
    private const string RestoredSuffixFormat = "_restored_{0}";

    /// <summary>回收站 $R 文件模式</summary>
    private const string RecycleBinFilePattern = "$R*";

    public BackupManager(
        ILogger<BackupManager> logger,
        ILocalTombstoneRepository tombstoneRepo,
        IDeletionRecordRepository deletionRecordRepo,
        ITombstoneCache tombstoneCache,
        IConfigService configService,
        IQuarantineManager quarantineManager)
        : base(logger)
    {
        _tombstoneRepo = tombstoneRepo ?? throw new ArgumentNullException(nameof(tombstoneRepo));
        _deletionRecordRepo = deletionRecordRepo ?? throw new ArgumentNullException(nameof(deletionRecordRepo));
        _tombstoneCache = tombstoneCache ?? throw new ArgumentNullException(nameof(tombstoneCache));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _quarantineManager = quarantineManager ?? throw new ArgumentNullException(nameof(quarantineManager));
        _shellFileOp = new ShellFileOperation(logger);
    }

    // ================================================================
    // IBackupManager 实现 — 回滚操作
    // ================================================================

    /// <inheritdoc/>
    public async Task<OperationResult> RollbackAsync(
        string operationId,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        Logger.LogMethodEntry(operationId);

        // 参数校验：operationId 为空时快速失败
        if (string.IsNullOrEmpty(operationId))
        {
            throw new ArgumentException("操作 ID 不能为 null 或空字符串。", nameof(operationId));
        }

        // 加载该操作批次的所有删除记录
        var records = await _deletionRecordRepo.GetByOperationIdAsync(operationId, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            Logger.LogWarning("[回滚] 未找到操作批次记录: OperationId={OpId}", operationId);
            return new OperationResult
            {
                OperationId = operationId,
                SuccessCount = 0,
                FailedCount = 0,
                ManualReviewCount = 0,
                CancelledCount = 0,
                FreedBytes = 0,
                Elapsed = TimeSpan.Zero
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var successCount = 0;
        var failedCount = 0;
        var restoredBytes = 0L;
        var failedFiles = new List<FileOperationError>();
        var lastProgressTime = Stopwatch.GetTimestamp();
        var progressIntervalTicks = ProgressReportIntervalMs * Stopwatch.Frequency / 1000;

        // 回收站恢复时的 SHA-256 缓存：避免同批次中多次重复计算同一个回收站文件
        var recycleBinHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var i = 0; i < records.Count; i++)
            {
                // 检查取消令牌（每个文件处理前）
                ct.ThrowIfCancellationRequested();

                var record = records[i];
                RestoreFileResult result;

                try
                {
                    // 根据删除方式选择恢复策略
                    result = record.DeletionMethod switch
                    {
                        DeleteMethod.Quarantine => await TryRestoreFromQuarantineAsync(record, ct).ConfigureAwait(false),
                        DeleteMethod.RecycleBin => await TryRestoreFromRecycleBinAsync(record, recycleBinHashCache, ct)
                            .ConfigureAwait(false),
                        _ => new RestoreFileResult
                        {
                            Success = false,
                            NotFound = true,
                            ErrorCode = "PERMANENT_DELETION",
                            ErrorMessage = "文件已被永久删除，无法恢复"
                        }
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个文件恢复失败不中断整个批次
                    Logger.LogWarning("[回滚] 文件恢复异常: {FilePath}, Error={Error}",
                        record.FilePath, ex.Message);
                    result = new RestoreFileResult
                    {
                        Success = false,
                        ErrorCode = "RESTORE_EXCEPTION",
                        ErrorMessage = ex.Message
                    };
                }

                // 统计结果
                if (result.Success)
                {
                    successCount++;
                    restoredBytes += result.FileSize;
                }
                else
                {
                    failedCount++;
                    failedFiles.Add(new FileOperationError
                    {
                        FilePath = record.FilePath,
                        ErrorCode = result.ErrorCode ?? "UNKNOWN",
                        ErrorMessage = result.ErrorMessage ?? "恢复失败"
                    });
                }

                // 进度报告（节流至 100ms）
                var nowTicks = Stopwatch.GetTimestamp();
                var isLastItem = i == records.Count - 1;
                if (isLastItem || nowTicks - lastProgressTime >= progressIntervalTicks)
                {
                    lastProgressTime = nowTicks;
                    progress?.Report(new OperationProgress
                    {
                        Phase = OperationPhase.Restoring,
                        CurrentFilePath = record.FilePath,
                        CompletedCount = successCount + failedCount,
                        TotalCount = records.Count,
                        ProcessedBytes = restoredBytes
                    });
                }
            }

            // 恢复成功后清理墓碑
            if (successCount > 0)
            {
                await CleanupTombstonesAsync(operationId, ct).ConfigureAwait(false);
            }

            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Completed,
                CompletedCount = records.Count,
                TotalCount = records.Count,
                ProcessedBytes = restoredBytes
            });

            stopwatch.Stop();
            Logger.LogMethodExit(nameof(RollbackAsync),
                $"OpId={operationId}, Success={successCount}, Failed={failedCount}, Bytes={restoredBytes}, Elapsed={stopwatch.Elapsed.TotalSeconds:F2}s");

            return new OperationResult
            {
                OperationId = operationId,
                SuccessCount = successCount,
                FailedCount = failedCount,
                ManualReviewCount = 0,
                CancelledCount = 0,
                FailedFiles = failedFiles.AsReadOnly(),
                FreedBytes = restoredBytes,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Logger.LogWarning("[回滚] 操作已被取消: OperationId={OpId}, 已恢复 {SuccessCount} 个文件",
                operationId, successCount);

            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Cancelled,
                CompletedCount = successCount + failedCount,
                TotalCount = records.Count,
                ProcessedBytes = restoredBytes
            });

            return new OperationResult
            {
                OperationId = operationId,
                SuccessCount = successCount,
                FailedCount = failedCount,
                CancelledCount = records.Count - successCount - failedCount,
                FailedFiles = failedFiles.AsReadOnly(),
                IsCancelled = true,
                FreedBytes = restoredBytes,
                Elapsed = stopwatch.Elapsed
            };
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationBatchInfo>> GetRollbackHistoryAsync(CancellationToken ct)
    {
        Logger.LogMethodEntry();
        var result = await _deletionRecordRepo.GetBatchSummariesAsync(limit: 50, ct).ConfigureAwait(false);
        Logger.LogMethodExit(nameof(GetRollbackHistoryAsync), $"返回 {result.Count} 个批次");
        return result;
    }

    /// <inheritdoc/>
    public async Task<SingleFileRestoreResult> RestoreQuarantineFileAsync(
        string quarantineFilePath,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        Logger.LogMethodEntry(quarantineFilePath);

        // 解析隔离区文件名，获取原始文件名用于结果反馈
        var fileName = Path.GetFileName(quarantineFilePath);
        var parsed = _quarantineManager.ParseQuarantineFileName(fileName);
        var displayName = parsed?.OriginalFileName ?? fileName;

        try
        {
            // 1. 校验文件存在
            if (!File.Exists(quarantineFilePath))
            {
                return new SingleFileRestoreResult
                {
                    Success = false,
                    NotFound = true,
                    FileSizeBytes = 0,
                    QuarantineFilePath = quarantineFilePath,
                    OriginalFileName = displayName,
                    ErrorMessage = "隔离区文件不存在，可能已被删除"
                };
            }

            // 进度报告：开始计算哈希
            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Hashing,
                CurrentFilePath = quarantineFilePath,
                CompletedCount = 0,
                TotalCount = 1,
                ProcessedBytes = 0
            });

            // 2. 计算隔离区文件的完整 SHA-256 哈希
            var fileHash = await ComputeFileHashAsync(quarantineFilePath, ct).ConfigureAwait(false);
            if (fileHash == null)
            {
                return new SingleFileRestoreResult
                {
                    Success = false,
                    FileSizeBytes = 0,
                    QuarantineFilePath = quarantineFilePath,
                    OriginalFileName = displayName,
                    ErrorMessage = "无法读取隔离区文件以计算哈希"
                };
            }

            // 3. 在 DeletionRecord 中按哈希查找原始完整路径
            var records = await _deletionRecordRepo.GetByFileHashAsync(fileHash, ct).ConfigureAwait(false);
            if (records.Count == 0)
            {
                return new SingleFileRestoreResult
                {
                    Success = false,
                    NotFound = true,
                    FileSizeBytes = new FileInfo(quarantineFilePath).Length,
                    QuarantineFilePath = quarantineFilePath,
                    OriginalFileName = displayName,
                    ErrorMessage = "未在删除记录中找到此文件的原始路径信息，无法恢复"
                };
            }

            // 4. 取最近的删除记录作为恢复目标路径
            var targetRecord = records[0];
            var fileSize = new FileInfo(quarantineFilePath).Length;

            // 进度报告：开始恢复
            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Restoring,
                CurrentFilePath = quarantineFilePath,
                CompletedCount = 0,
                TotalCount = 1,
                ProcessedBytes = 0
            });

            // 5. 解决目标路径冲突（复用现有 ResolveRestorePath 方法）
            string resolvedPath;
            try
            {
                resolvedPath = ResolveRestorePath(targetRecord.FilePath);
            }
            catch (IOException ex)
            {
                return new SingleFileRestoreResult
                {
                    Success = false,
                    FileSizeBytes = fileSize,
                    QuarantineFilePath = quarantineFilePath,
                    OriginalFileName = displayName,
                    ErrorMessage = $"无法解决路径冲突: {ex.Message}"
                };
            }

            // 6. 尝试 ShellFileOperation 移动文件 → 失败回退到 File.Move
            var moveHr = _shellFileOp.MoveFile(quarantineFilePath, resolvedPath);
            if (moveHr != Shell32Native.S_OK)
            {
                // ShellFileOperation 失败时尝试 System.IO.File.Move 作为回退
                try
                {
                    var denormSrc = PathNormalizer.Denormalize(quarantineFilePath);
                    var denormDst = PathNormalizer.Denormalize(resolvedPath);
                    var dstDir = Path.GetDirectoryName(denormDst);
                    if (dstDir != null && !Directory.Exists(dstDir))
                    {
                        Directory.CreateDirectory(dstDir);
                    }
                    File.Move(denormSrc, denormDst);
                }
                catch (Exception ex)
                {
                    return new SingleFileRestoreResult
                    {
                        Success = false,
                        FileSizeBytes = fileSize,
                        QuarantineFilePath = quarantineFilePath,
                        OriginalFileName = displayName,
                        ErrorMessage = $"文件移动失败: {ShellFileOperation.GetErrorMessage(moveHr)}; File.Move 回退: {ex.Message}"
                    };
                }
            }

            // 进度报告：完成
            progress?.Report(new OperationProgress
            {
                Phase = OperationPhase.Completed,
                CurrentFilePath = quarantineFilePath,
                CompletedCount = 1,
                TotalCount = 1,
                ProcessedBytes = fileSize
            });

            Logger.LogOperation("QuarantineSingleRestore", quarantineFilePath,
                $"成功恢复到 {resolvedPath}");

            return new SingleFileRestoreResult
            {
                Success = true,
                RestoredPath = resolvedPath,
                FileSizeBytes = fileSize,
                QuarantineFilePath = quarantineFilePath,
                OriginalFileName = displayName
            };
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("[隔离区单文件恢复] 操作被取消: {File}", quarantineFilePath);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[隔离区单文件恢复] 恢复失败: {File}", quarantineFilePath);
            return new SingleFileRestoreResult
            {
                Success = false,
                FileSizeBytes = 0,
                QuarantineFilePath = quarantineFilePath,
                OriginalFileName = displayName,
                ErrorMessage = $"恢复异常: {ex.Message}"
            };
        }
    }

    /// <inheritdoc/>
    public async Task<HashVerificationResult> VerifyHashesAsync(string operationId, CancellationToken ct)
    {
        Logger.LogMethodEntry(operationId);

        // 加载删除记录
        var records = await _deletionRecordRepo.GetByOperationIdAsync(operationId, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            Logger.LogWarning("[哈希校验] 未找到操作批次记录: OperationId={OpId}", operationId);
            return new HashVerificationResult
            {
                PassedCount = 0,
                FailedCount = 0,
                NotFoundCount = 0
            };
        }

        var passedCount = 0;
        var failedCount = 0;
        var notFoundCount = 0;
        var failures = new List<HashMismatchEntry>();

        // 回收站哈希缓存
        var recycleBinHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();

            // 尝试定位文件位置
            var (found, locationPath) = await LocateRecoverableFileAsync(
                record, recycleBinHashCache, ct).ConfigureAwait(false);

            if (!found || locationPath == null)
            {
                notFoundCount++;
                failures.Add(new HashMismatchEntry
                {
                    FilePath = record.FilePath,
                    ExpectedHash = record.FileHash,
                    ActualHash = "N/A",
                    Reason = GetNotFoundReason(record.DeletionMethod)
                });
                continue;
            }

            // 验证哈希
            try
            {
                var actualHash = await ComputeFileHashAsync(locationPath, ct).ConfigureAwait(false);
                if (actualHash == null)
                {
                    notFoundCount++;
                    failures.Add(new HashMismatchEntry
                    {
                        FilePath = record.FilePath,
                        ExpectedHash = record.FileHash,
                        ActualHash = "N/A",
                        Reason = "无法读取文件以计算哈希"
                    });
                    continue;
                }

                if (string.Equals(actualHash, record.FileHash, StringComparison.OrdinalIgnoreCase))
                {
                    passedCount++;
                }
                else
                {
                    failedCount++;
                    failures.Add(new HashMismatchEntry
                    {
                        FilePath = record.FilePath,
                        ExpectedHash = record.FileHash,
                        ActualHash = actualHash,
                        Reason = "SHA-256 哈希不匹配，文件可能已损坏或被篡改"
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                notFoundCount++;
                failures.Add(new HashMismatchEntry
                {
                    FilePath = record.FilePath,
                    ExpectedHash = record.FileHash,
                    ActualHash = "N/A",
                    Reason = $"哈希计算异常: {ex.Message}"
                });
            }
        }

        Logger.LogMethodExit(nameof(VerifyHashesAsync),
            $"OpId={operationId}, Passed={passedCount}, Failed={failedCount}, NotFound={notFoundCount}");

        return new HashVerificationResult
        {
            PassedCount = passedCount,
            FailedCount = failedCount,
            NotFoundCount = notFoundCount,
            Failures = failures.AsReadOnly()
        };
    }

    // ================================================================
    // 私有方法 — 隔离区恢复
    // ================================================================

    /// <summary>
    /// 从隔离区恢复文件。
    ///
    /// 流程：
    /// 1. 获取隔离区目录路径。
    /// 2. 根据记录构造搜索模式 {SHA256前16位}_{文件名}_*.quarantine。
    /// 3. 枚举匹配文件，计算 SHA-256 找到哈希完全匹配的文件。
    /// 4. 解决目标路径冲突（原路径存在时添加 _restored_N 后缀）。
    /// 5. 使用 ShellFileOperation.MoveFile 移动文件回原路径。
    /// </summary>
    private async Task<RestoreFileResult> TryRestoreFromQuarantineAsync(
        DeletionRecord record, CancellationToken ct)
    {
        // B6 重构：通过 QuarantineManager 统一获取隔离区目录
        var quarantineDir = await _quarantineManager.GetQuarantineDirectoryAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(quarantineDir) || !Directory.Exists(quarantineDir))
        {
            return new RestoreFileResult
            {
                Success = false,
                NotFound = true,
                ErrorCode = "QUARANTINE_DISABLED",
                ErrorMessage = "隔离区功能已禁用或目录不可访问"
            };
        }

        // 构造搜索模式：{SHA256前16位}_{文件名}_*.quarantine
        var sha256Prefix = record.FileHash.Length >= 16
            ? record.FileHash[..16]
            : record.FileHash;
        var denormalizedPath = PathNormalizer.Denormalize(record.FilePath);
        var originalFileName = Path.GetFileName(denormalizedPath);

        // 搜索隔离区中的匹配文件
        var searchPattern = $"{sha256Prefix}_{originalFileName}_*.quarantine";
        string? matchedFile = null;

        try
        {
            foreach (var file in Directory.EnumerateFiles(quarantineDir, searchPattern))
            {
                ct.ThrowIfCancellationRequested();

                // 校验找到的文件哈希是否与记录一致
                var hashOk = await HashVerifier.VerifyHashAsync(file, record.FileHash, ct)
                    .ConfigureAwait(false);
                if (hashOk)
                {
                    matchedFile = file;
                    break; // 找到第一个哈希匹配的文件即停止
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[隔离区恢复] 枚举隔离区文件异常: {QuarantineDir}, Error={Error}",
                quarantineDir, ex.Message);
        }

        if (matchedFile == null)
        {
            return new RestoreFileResult
            {
                Success = false,
                NotFound = true,
                ErrorCode = "QUARANTINE_FILE_NOT_FOUND",
                ErrorMessage = $"在隔离区中未找到匹配的文件，搜索模式: {searchPattern}"
            };
        }

        // 解决目标路径冲突
        string resolvedPath;
        try
        {
            resolvedPath = ResolveRestorePath(record.FilePath);
        }
        catch (IOException ex)
        {
            return new RestoreFileResult
            {
                Success = false,
                ErrorCode = "CONFLICT_RESOLUTION_FAILED",
                ErrorMessage = ex.Message
            };
        }

        // 移动文件（同卷原子，跨卷由 ShellFileOperation 处理）
        var moveHr = _shellFileOp.MoveFile(matchedFile, resolvedPath);
        if (moveHr == Shell32Native.S_OK)
        {
            Logger.LogOperation("QuarantineRestore", record.FilePath,
                $"成功恢复 → {resolvedPath}");
            return new RestoreFileResult
            {
                Success = true,
                RestoredPath = resolvedPath,
                FileSize = record.FileSize
            };
        }

        // ShellFileOperation 失败时尝试 System.IO.File.Move 作为回退
        try
        {
            var denormSrc = PathNormalizer.Denormalize(matchedFile);
            var denormDst = PathNormalizer.Denormalize(resolvedPath);
            var dstDir = Path.GetDirectoryName(denormDst);
            if (dstDir != null && !Directory.Exists(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }
            File.Move(denormSrc, denormDst);

            Logger.LogOperation("QuarantineRestore", record.FilePath,
                $"通过 File.Move 回退恢复成功 → {resolvedPath}");
            return new RestoreFileResult
            {
                Success = true,
                RestoredPath = resolvedPath,
                FileSize = record.FileSize
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[隔离区恢复] 文件移动失败: {Source} → {Dest}",
                matchedFile, resolvedPath);
            return new RestoreFileResult
            {
                Success = false,
                ErrorCode = $"MOVE_FAILED_0x{moveHr:X8}",
                ErrorMessage = $"文件移动失败: {ShellFileOperation.GetErrorMessage(moveHr)}"
            };
        }
    }

    // ================================================================
    // 私有方法 — 回收站恢复
    // ================================================================

    /// <summary>
    /// 从回收站恢复文件（次要路径）。
    ///
    /// 回收站结构：{drive}\$Recycle.Bin\{sid}\$R*
    /// 通过枚举 $R 文件并计算 SHA-256 哈希来匹配目标文件。
    /// 使用缓存字典避免同批次中重复计算同一文件的哈希。
    ///
    /// 限制：回收站枚举在大回收站中较慢（O(n*m)），但回收站文件一般不会数量巨大。
    /// </summary>
    private async Task<RestoreFileResult> TryRestoreFromRecycleBinAsync(
        DeletionRecord record,
        Dictionary<string, string> hashCache,
        CancellationToken ct)
    {
        // 获取卷根路径和用户 SID
        var denormalizedPath = PathNormalizer.Denormalize(record.FilePath);
        var driveRoot = Path.GetPathRoot(denormalizedPath);
        if (string.IsNullOrEmpty(driveRoot))
        {
            return new RestoreFileResult
            {
                Success = false,
                NotFound = true,
                ErrorCode = "RECYCLE_BIN_INVALID_PATH",
                ErrorMessage = $"无法解析文件路径的驱动器: {record.FilePath}"
            };
        }

        var userSid = GetCurrentUserSid();
        var recycleBinDir = Path.Combine(driveRoot, "$Recycle.Bin", userSid);

        if (!Directory.Exists(recycleBinDir))
        {
            return new RestoreFileResult
            {
                Success = false,
                NotFound = true,
                ErrorCode = "RECYCLE_BIN_NOT_FOUND",
                ErrorMessage = $"回收站目录不存在或无权访问: {recycleBinDir}"
            };
        }

        // 先检查缓存：该哈希值是否已匹配过某个回收站文件
        if (hashCache.TryGetValue(record.FileHash, out var cachedPath))
        {
            if (File.Exists(cachedPath))
            {
                var resolvedPath = ResolveRestorePath(record.FilePath);
                return await MoveRecycleBinFileAsync(cachedPath, resolvedPath, record.FileSize, ct)
                    .ConfigureAwait(false);
            }
            // 缓存路径已失效，移除
            hashCache.Remove(record.FileHash);
        }

        // 枚举回收站中的 $R 文件，计算哈希匹配
        try
        {
            foreach (var rFile in Directory.EnumerateFiles(recycleBinDir, RecycleBinFilePattern))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 计算回收站文件的 SHA-256 哈希（同时填充缓存）
                    var fileHash = await ComputeFileHashAsync(rFile, ct).ConfigureAwait(false);
                    if (fileHash == null)
                        continue;

                    // 无论是否匹配，都加入缓存（避免后续文件重复计算）
                    if (!hashCache.ContainsKey(fileHash))
                    {
                        hashCache[fileHash] = rFile;
                    }

                    if (string.Equals(fileHash, record.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var resolvedPath = ResolveRestorePath(record.FilePath);
                        return await MoveRecycleBinFileAsync(rFile, resolvedPath, record.FileSize, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单个回收站文件哈希计算失败不影响其他文件
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[回收站恢复] 枚举回收站文件异常: {RecycleBinDir}, Error={Error}",
                recycleBinDir, ex.Message);
        }

        return new RestoreFileResult
        {
            Success = false,
            NotFound = true,
            ErrorCode = "RECYCLE_BIN_FILE_NOT_FOUND",
            ErrorMessage = $"在回收站中未找到哈希匹配的文件: {record.FilePath}"
        };
    }

    /// <summary>
    /// 将回收站文件移动到目标路径。
    /// 回收站文件通常为只读，需要先移除只读属性再移动。
    /// </summary>
    private static async Task<RestoreFileResult> MoveRecycleBinFileAsync(
        string sourcePath, string destPath, long fileSize, CancellationToken ct)
    {
        try
        {
            // 确保目标目录存在
            var destDir = Path.GetDirectoryName(PathNormalizer.Denormalize(destPath));
            if (destDir != null && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // 移除回收站文件的只读属性
            var fileInfo = new FileInfo(sourcePath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                fileInfo.Attributes &= ~FileAttributes.ReadOnly;
            }

            var denormSrc = PathNormalizer.Denormalize(sourcePath);
            var denormDst = PathNormalizer.Denormalize(destPath);
            await Task.Run(() => File.Move(denormSrc, denormDst), ct).ConfigureAwait(false);

            return new RestoreFileResult
            {
                Success = true,
                RestoredPath = destPath,
                FileSize = fileSize
            };
        }
        catch (Exception ex)
        {
            return new RestoreFileResult
            {
                Success = false,
                ErrorCode = "RECYCLE_BIN_MOVE_FAILED",
                ErrorMessage = $"从回收站恢复文件失败: {ex.Message}"
            };
        }
    }

    // ================================================================
    // 私有方法 — 文件定位（用于 VerifyHashesAsync）
    // ================================================================

    /// <summary>
    /// 定位可恢复文件的位置。
    /// 返回 (是否找到, 文件路径)。
    /// 按隔离区 → 回收站 → 未找到的顺序查找。
    /// </summary>
    private async Task<(bool Found, string? Path)> LocateRecoverableFileAsync(
        DeletionRecord record,
        Dictionary<string, string> hashCache,
        CancellationToken ct)
    {
        // 先查隔离区
        if (record.DeletionMethod == DeleteMethod.Quarantine)
        {
            // B6 重构：通过 QuarantineManager 统一获取隔离区目录
            var quarantineDir = await _quarantineManager.GetQuarantineDirectoryAsync(ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(quarantineDir) && Directory.Exists(quarantineDir))
            {
                var sha256Prefix = record.FileHash.Length >= 16
                    ? record.FileHash[..16]
                    : record.FileHash;
                var denormalizedPath = PathNormalizer.Denormalize(record.FilePath);
                var originalFileName = Path.GetFileName(denormalizedPath);
                var searchPattern = $"{sha256Prefix}_{originalFileName}_*.quarantine";

                foreach (var file in Directory.EnumerateFiles(quarantineDir, searchPattern))
                {
                    ct.ThrowIfCancellationRequested();
                    var hashOk = await HashVerifier.VerifyHashAsync(file, record.FileHash, ct)
                        .ConfigureAwait(false);
                    if (hashOk)
                        return (true, file);
                }
            }
        }

        // 再查回收站
        if (record.DeletionMethod == DeleteMethod.RecycleBin)
        {
            var denormalizedPath = PathNormalizer.Denormalize(record.FilePath);
            var driveRoot = Path.GetPathRoot(denormalizedPath);
            if (!string.IsNullOrEmpty(driveRoot))
            {
                var userSid = GetCurrentUserSid();
                var recycleBinDir = Path.Combine(driveRoot, "$Recycle.Bin", userSid);

                if (Directory.Exists(recycleBinDir))
                {
                    // 先查缓存
                    if (hashCache.TryGetValue(record.FileHash, out var cachedPath)
                        && File.Exists(cachedPath))
                    {
                        return (true, cachedPath);
                    }

                    foreach (var rFile in Directory.EnumerateFiles(recycleBinDir, "$R*"))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var fileHash = await ComputeFileHashAsync(rFile, ct).ConfigureAwait(false);
                            if (fileHash != null && !hashCache.ContainsKey(fileHash))
                            {
                                hashCache[fileHash] = rFile;
                            }
                            if (fileHash != null && string.Equals(
                                fileHash, record.FileHash, StringComparison.OrdinalIgnoreCase))
                            {
                                return (true, rFile);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* 继续 */ }
                    }
                }
            }
        }

        return (false, null);
    }

    // ================================================================
    // 私有方法 — 辅助
    // ================================================================

    /// <summary>
    /// 解决恢复目标路径冲突。
    /// 若原路径不存在则直接使用；若存在则在文件名后添加 _restored_N 后缀。
    /// 最多尝试 99 次，超过则抛出 IOException。
    /// </summary>
    /// <param name="originalPath">\\?\ 格式的原始文件路径</param>
    /// <returns>\\?\ 格式的可用目标路径</returns>
    private static string ResolveRestorePath(string originalPath)
    {
        // 检查原路径是否空闲
        if (!File.Exists(originalPath) && !Directory.Exists(originalPath))
            return originalPath;

        // 原路径被占用，尝试添加后缀
        var denormalizedPath = PathNormalizer.Denormalize(originalPath);
        var directory = Path.GetDirectoryName(denormalizedPath) ?? ".";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(denormalizedPath);
        var extension = Path.GetExtension(denormalizedPath);

        for (var i = 1; i <= MaxConflictRetry; i++)
        {
            var candidate = Path.Combine(
                directory,
                $"{fileNameWithoutExt}{string.Format(CultureInfo.InvariantCulture, RestoredSuffixFormat, i)}{extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return PathNormalizer.Normalize(candidate);
        }

        throw new IOException(
            $"无法为 '{denormalizedPath}' 找到可用的恢复路径（已尝试 {MaxConflictRetry} 次）");
    }

    /// <summary>
    /// 获取当前用户的 SID 字符串。
    /// 回收站目录按用户 SID 隔离，需要此值来定位回收站路径。
    /// 失败时返回 "UNKNOWN"。
    /// </summary>
    private static string GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希值（用于回收站文件匹配）。
    ///
    /// 使用流式读取（4KB 缓冲区），与 HashVerifier.ComputeSha256Async 保持一致。
    /// 回收站文件通常较大，流式处理避免加载整个文件到内存。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>小写十六进制哈希字符串，失败返回 null</returns>
    private static async Task<string?> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        try
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var hashBytes = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 获取文件"未找到"原因的人类可读描述。
    /// </summary>
    private static string GetNotFoundReason(DeleteMethod deletionMethod)
    {
        return deletionMethod switch
        {
            DeleteMethod.Permanent => "文件已被永久删除，无恢复来源",
            DeleteMethod.Quarantine => "隔离区中未找到匹配文件（可能已过期清理或隔离区已禁用）",
            DeleteMethod.RecycleBin => "回收站中未找到匹配文件（可能已被清空或权限不足）",
            _ => "未知删除方式，无法定位文件"
        };
    }

    /// <summary>
    /// 清理墓碑记录：从数据库和内存缓存中移除指定操作批次的所有墓碑。
    /// </summary>
    private async Task CleanupTombstonesAsync(string operationId, CancellationToken ct)
    {
        try
        {
            // 先刷新待写入队列以确保所有墓碑已落盘
            await _tombstoneRepo.FlushAsync(ct).ConfigureAwait(false);

            // 按操作 ID 批量删除数据库中的墓碑
            var deletedCount = await _tombstoneRepo.DeleteByOperationIdAsync(operationId, ct)
                .ConfigureAwait(false);

            // 同步清除内存缓存
            _tombstoneCache.RemoveByOperationId(operationId);

            Logger.LogOperation("TombstoneCleanup", operationId,
                $"SUCCESS, 删除 {deletedCount} 条墓碑记录");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[墓碑清除] 清理墓碑失败: OperationId={OpId}", operationId);
            // 墓碑清除失败不影响恢复结果（文件已恢复，墓碑将随过期机制自动清理）
        }
    }

    // ================================================================
    // 内部结构体 — 单文件恢复结果
    // ================================================================

    /// <summary>
    /// 单个文件恢复操作的内部结果。
    /// 用于汇总 RollbackAsync 中的每个文件恢复结果。
    /// </summary>
    private readonly struct RestoreFileResult
    {
        /// <summary>恢复是否成功</summary>
        public bool Success { get; init; }

        /// <summary>文件未找到（回收站/隔离区中无匹配）</summary>
        public bool NotFound { get; init; }

        /// <summary>错误码（HRESULT 字符串或自定义码）</summary>
        public string? ErrorCode { get; init; }

        /// <summary>错误描述信息</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>恢复后的文件路径</summary>
        public string? RestoredPath { get; init; }

        /// <summary>恢复的文件大小（字节）</summary>
        public long FileSize { get; init; }
    }
}
