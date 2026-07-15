using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 分析协调器实现。管理请求去重、进度聚合、取消和通知策略。
///
/// 线程安全：ConcurrentDictionary 用于去重集合，lock 保护 CTS 和进度节流。
/// 单例生命周期，所有 ViewModel 共享同一实例。
/// </summary>
public sealed class AiAnalysisCoordinator : IAiAnalysisCoordinator, IDisposable
{
    private readonly IAiAdvisorService _aiAdvisor;
    private readonly IConfigService _configService;
    private readonly ILogger<AiAnalysisCoordinator> _logger;

    // 去重：记录已完成的文件路径
    private readonly ConcurrentDictionary<string, AnalysisState> _fileStates = new();

    // 批量/自动分析的取消令牌源
    private CancellationTokenSource? _batchCts;
    private readonly object _batchLock = new();

    // 进度节流
    private AiAnalysisProgress? _currentProgress;
    private long _lastProgressTicks; // Stopwatch.GetTimestamp() ticks
    private static readonly long MinProgressIntervalTicks = Stopwatch.Frequency / 5; // 200ms
    private readonly object _progressLock = new();

    private bool _disposed;

    // 进度事件
    public event EventHandler<AiAnalysisProgress>? ProgressChanged;
    public event EventHandler<AiAnalysisCompletedEventArgs>? AnalysisCompleted;

    // 文件状态枚举
    private enum AnalysisState { Pending, Completed, Failed }

    public AiAnalysisCoordinator(
        IAiAdvisorService aiAdvisor,
        IConfigService configService,
        ILogger<AiAnalysisCoordinator> logger)
    {
        _aiAdvisor = aiAdvisor ?? throw new ArgumentNullException(nameof(aiAdvisor));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("AI 分析协调器初始化完成");
    }

    // ============================================================
    // 公开属性
    // ============================================================

    public bool IsAnalyzing
    {
        get
        {
            lock (_batchLock)
            {
                return _batchCts != null && !_batchCts.IsCancellationRequested;
            }
        }
    }

    public AiAnalysisProgress? CurrentProgress
    {
        get
        {
            lock (_progressLock)
            {
                return _currentProgress;
            }
        }
    }

    // ============================================================
    // 单文件分析
    // ============================================================

    public async Task<AiAnalysisResult?> AnalyzeSingleAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"文件={filePath}");
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            if (!_aiAdvisor.IsAvailable)
            {
                _logger.LogWarning("AI 服务不可用，单文件分析被跳过: {Path}", filePath);
                return null;
            }

            return await _aiAdvisor.AnalyzeFileAsync(filePath, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("单文件 AI 分析被取消: {Path}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "单文件 AI 分析异常: {Path}", filePath);
            return null;
        }
    }

    // ============================================================
    // 批量分析
    // ============================================================

    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeBatchAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"文件数={files?.Count ?? 0}");
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        // 检查并发 — 仅允许一个批量/自动分析运行
        if (IsAnalyzing)
        {
            _logger.LogWarning("AI 分析进行中，请稍后重试");
            return Array.Empty<AiAnalysisResult>();
        }

        // 去重 — 过滤已分析过的文件
        var pending = files
            .Where(f => _fileStates.TryAdd(f.FilePath, AnalysisState.Pending))
            .ToList();

        var skipped = files.Count - pending.Count;
        if (skipped > 0)
            _logger.LogInformation("去重跳过 {Count} 个已分析或正在分析的文件", skipped);

        if (pending.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        return await RunBatchWithProgressAsync(pending, ct);
    }

    // ============================================================
    // 自动分析
    // ============================================================

    public async Task<IReadOnlyList<AiAnalysisResult>> TriggerAutoAnalyzeAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"全量文件数={allFiles?.Count ?? 0}");

        if (allFiles == null || allFiles.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        // 前置条件检查
        var config = _configService.Current;

        if (!config.AIEnabled)
        {
            _logger.LogDebug("自动分析跳过: AI 未启用");
            return Array.Empty<AiAnalysisResult>();
        }

        if (!config.AIAutoAnalyze)
        {
            _logger.LogDebug("自动分析跳过: AIAutoAnalyze 未启用");
            return Array.Empty<AiAnalysisResult>();
        }

        if (config.UserMode != UserMode.Expert)
        {
            _logger.LogDebug("自动分析跳过: 非专家模式 (当前={Mode})", config.UserMode);
            return Array.Empty<AiAnalysisResult>();
        }

        if (!_aiAdvisor.IsAvailable)
        {
            _logger.LogInformation("自动分析跳过: AI 服务不可用");
            return Array.Empty<AiAnalysisResult>();
        }

        // 筛选未分类大文件（与 IAiAdvisorService.AutoAnalyzeUncategorizedAsync 筛选规则一致）
        var pending = allFiles
            .Where(f => string.IsNullOrEmpty(f.SemanticCategory)
                     || f.SemanticCategory.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.SizeBytes > 100L * 1024 * 1024)       // > 100 MB
            .Where(f => string.IsNullOrEmpty(f.AiLabel))         // 跳过已分析
            .OrderByDescending(f => f.SizeBytes)                 // 按大小降序
            .Take(AutoAnalyzeMaxFiles)                           // 最多 AutoAnalyzeMaxFiles 个
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("自动分析: 没有符合条件的未分类大文件（>100MB 且未分析）");
            return Array.Empty<AiAnalysisResult>();
        }

        _logger.LogInformation("自动分析: 筛选出 {Count} 个未分类大文件（>100MB），开始批量分析", pending.Count);

        // 去重 — 过滤已分析过的文件（去重字典）
        var deduped = pending
            .Where(f => _fileStates.TryAdd(f.FilePath, AnalysisState.Pending))
            .ToList();

        if (deduped.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        // 通过统一的批量管道执行，确保进度事件、取消支持和完成通知的一致性
        return await RunBatchWithProgressAsync(deduped, ct);
    }

    // ============================================================
    // 去重状态管理
    // ============================================================

    private const int AutoAnalyzeMaxFiles = 500; // 自动分析单次最多 500 文件

    /// <summary>
    /// 清除内部去重状态字典。新扫描开始时调用，防止 _fileStates 无限增长。
    /// 线程安全：ConcurrentDictionary.Clear() 是原子操作。
    /// </summary>
    public void ClearFileStates()
    {
        _fileStates.Clear();
        _logger.LogDebug("文件分析状态字典已清除");
    }

    // ============================================================
    // 取消
    // ============================================================

    public void CancelCurrentAnalysis()
    {
        lock (_batchLock)
        {
            if (_batchCts == null || _batchCts.IsCancellationRequested)
            {
                _logger.LogDebug("没有进行中的分析可取消");
                return;
            }

            _logger.LogInformation("用户请求取消当前 AI 分析");
            _batchCts.Cancel();
        }
    }

    // ============================================================
    // 内部批量执行
    // ============================================================

    private async Task<IReadOnlyList<AiAnalysisResult>> RunBatchWithProgressAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken externalCt)
    {
        // 创建内部 CTS（链接外部令牌）
        lock (_batchLock)
        {
            _batchCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        }

        CancellationTokenSource batchCts;
        lock (_batchLock)
        {
            batchCts = _batchCts!;
        }

        var totalCount = files.Count;
        IReadOnlyList<AiAnalysisResult> batchResults = Array.Empty<AiAnalysisResult>();

        try
        {
            // 更新初始进度
            UpdateProgress(totalCount, 0, 0, null, false);

            // 调用批量分析方法
            batchResults = await _aiAdvisor.AnalyzeFilesAsync(files, batchCts.Token);

            // 根据结果更新文件状态
            foreach (var result in batchResults)
            {
                if (result.IsSuccess)
                {
                    _fileStates[result.FilePath] = AnalysisState.Completed;
                }
                else
                {
                    _fileStates[result.FilePath] = AnalysisState.Failed;
                }
            }

            // 标记未返回结果的文件为失败（防御性编码）
            foreach (var file in files)
            {
                if (batchResults.All(r => r.FilePath != file.FilePath))
                {
                    _fileStates[file.FilePath] = AnalysisState.Failed;
                }
            }

            var completedCount = batchResults.Count(r => r.IsSuccess);
            var failedCount = batchResults.Count - completedCount;

            // 最终进度
            UpdateProgress(totalCount, completedCount, failedCount, null, false);

            // 触发完成事件
            OnAnalysisCompleted(new AiAnalysisCompletedEventArgs
            {
                SuccessCount = completedCount,
                FailedCount = failedCount,
                WasCancelled = false
            });

            _logger.LogInformation("批量 AI 分析完成: 成功={Success}, 失败={Failed}, 总计={Total}",
                completedCount, failedCount, totalCount);

            return batchResults;
        }
        catch (OperationCanceledException)
        {
            // 取消情况下，尝试从 batchResults 统计已完成的
            var completedCount = 0;
            var failedCount = 0;
            if (batchResults != null)
            {
                completedCount = batchResults.Count(r => r.IsSuccess);
                failedCount = batchResults.Count - completedCount;
            }

            _logger.LogInformation("批量 AI 分析被取消: 已完成={Completed}, 失败={Failed}",
                completedCount, failedCount);

            UpdateProgress(totalCount, completedCount, failedCount, null, true);

            OnAnalysisCompleted(new AiAnalysisCompletedEventArgs
            {
                SuccessCount = completedCount,
                FailedCount = failedCount,
                WasCancelled = true
            });

            return batchResults ?? Array.Empty<AiAnalysisResult>();
        }
        finally
        {
            lock (_batchLock)
            {
                batchCts.Dispose();
                _batchCts = null;
            }
        }
    }

    // ============================================================
    // 进度管理
    // ============================================================

    private void UpdateProgress(int total, int completed, int failed,
        string? currentFilePath, bool isCancelRequested)
    {
        lock (_progressLock)
        {
            _currentProgress = new AiAnalysisProgress
            {
                TotalCount = total,
                CompletedCount = completed,
                FailedCount = failed,
                CurrentFilePath = currentFilePath,
                IsCancelRequested = isCancelRequested
            };

            // 节流检查
            var nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks - _lastProgressTicks >= MinProgressIntervalTicks)
            {
                _lastProgressTicks = nowTicks;
                var snapshot = _currentProgress; // 捕获快照避免闭包引用脏读
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    ProgressChanged?.Invoke(this, snapshot);
                });
            }
        }
    }

    private void OnAnalysisCompleted(AiAnalysisCompletedEventArgs args)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            AnalysisCompleted?.Invoke(this, args);
        });
    }

    // ============================================================
    // IDisposable
    // ============================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_batchLock)
        {
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = null;
        }

        _logger.LogDebug("AiAnalysisCoordinator 已释放");
    }
}
