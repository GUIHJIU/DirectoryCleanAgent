# AI 分析功能完善 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 引入 `IAiAnalysisCoordinator` 协调器中间层，打通单文件/批量/自动分析三个 AI 入口，提供统一进度和取消支持。

**Architecture:** 新增 `AiAnalysisCoordinator` 单例服务，坐落于 ViewModels 与 `IAiAdvisorService` 之间。负责请求去重、进度聚合、事件广播、取消管理和通知策略。FileListViewModel 和 MainViewModel 通过协调器间接调用 AI 服务。

**Tech Stack:** C# .NET 8, WPF, 现有 `DirectoryCleanAgent.AI` / `DirectoryCleanAgent.Decision` / `DirectoryCleanAgent.Data` 层

## Global Constraints

- `IAiAnalysisCoordinator` 注册为单例生命周期
- 进度事件最小触发间隔 200ms（`Stopwatch` 节流）
- `TriggerAutoAnalyzeAsync` 前置条件：`UserMode == Expert && AIEnabled && AIAutoAnalyze && IsAvailable`
- 协调器单次只允许一个批量/自动分析运行中
- 单文件分析可与批量/自动分析并行
- 所有新增类型放在 `DirectoryCleanAgent.AI` 命名空间
- 遵循项目现有日志模式：`_logger.LogMethodEntry()` / `_logger.LogInformation()` / `_logger.LogWarning()` / `_logger.LogError()`

---

### Task 1: 创建进度与完成事件 DTO

**Files:**
- Create: `src/DirectoryCleanAgent.AI/Models/AiAnalysisProgress.cs`

**Interfaces:**
- Produces: `AiAnalysisProgress` (class), `AiAnalysisCompletedEventArgs` (class) — 供 Task 2 接口和 Task 3 实现使用

- [ ] **Step 1: 创建 DTO 文件**

```csharp
// src/DirectoryCleanAgent.AI/Models/AiAnalysisProgress.cs

namespace DirectoryCleanAgent.AI.Models;

/// <summary>
/// AI 分析进度快照，通过 IAiAnalysisCoordinator.ProgressChanged 事件广播。
/// 按 200ms 最小间隔节流发布。
/// </summary>
public sealed class AiAnalysisProgress
{
    /// <summary>总任务数</summary>
    public int TotalCount { get; init; }

    /// <summary>已完成任务数（含成功和失败）。SucceededCount = CompletedCount - FailedCount</summary>
    public int CompletedCount { get; init; }

    /// <summary>失败任务数（是 CompletedCount 的子集）</summary>
    public int FailedCount { get; init; }

    /// <summary>正在处理的文件完整路径，无时为 null</summary>
    public string? CurrentFilePath { get; init; }

    /// <summary>是否已请求取消</summary>
    public bool IsCancelRequested { get; init; }

    /// <summary>便捷属性：成功完成数 = CompletedCount - FailedCount</summary>
    public int SucceededCount => CompletedCount - FailedCount;
}

/// <summary>
/// 批量/自动 AI 分析完成事件参数。
/// </summary>
public sealed class AiAnalysisCompletedEventArgs : EventArgs
{
    /// <summary>成功完成数</summary>
    public int SuccessCount { get; init; }

    /// <summary>失败数</summary>
    public int FailedCount { get; init; }

    /// <summary>是否因用户取消而中止</summary>
    public bool WasCancelled { get; init; }
}
```

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent.AI
```

- [ ] **Step 3: 提交**

```bash
git add src/DirectoryCleanAgent.AI/Models/AiAnalysisProgress.cs
git commit -m "feat: add AiAnalysisProgress and AiAnalysisCompletedEventArgs DTOs

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 创建 IAiAnalysisCoordinator 接口

**Files:**
- Create: `src/DirectoryCleanAgent.AI/IAiAnalysisCoordinator.cs`

**Interfaces:**
- Consumes: `AiAnalysisProgress`, `AiAnalysisCompletedEventArgs` (Task 1)
- Produces: `IAiAnalysisCoordinator` — 供 Task 3 实现、Task 4 DI 注册、Task 5/6 ViewModel 注入

- [ ] **Step 1: 创建接口文件**

```csharp
// src/DirectoryCleanAgent.AI/IAiAnalysisCoordinator.cs

using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 分析协调器 — ViewModels 与 IAiAdvisorService 之间的中间层。
///
/// 职责：
///   - 请求去重（同一文件不重复分析）
///   - 进度聚合与事件广播（ProgressChanged / AnalysisCompleted）
///   - 取消管理（CancelCurrentAnalysis）
///   - 通知策略（前台状态栏 vs 后台 Toast）
///
/// 生命周期：单例，注册在 AiServiceRegistration.AddAiLayer()。
/// </summary>
public interface IAiAnalysisCoordinator
{
    // ============================================================
    // 操作入口
    // ============================================================

    /// <summary>
    /// 分析单个文件（UI 单文件"AI分析"按钮）。
    /// 不触发全局进度事件，不纳入去重集合。
    /// </summary>
    /// <returns>分析结果；AI 不可用时返回 null</returns>
    Task<AiAnalysisResult?> AnalyzeSingleAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// 分析批量文件（工具栏按钮 / 右键菜单触发）。
    /// 内部自动去重 + 跳过已有 AiLabel 的文件。
    /// 通过 ProgressChanged 事件广播进度。
    /// </summary>
    /// <param name="files">待分析的文件缓存列表</param>
    Task<IReadOnlyList<AiAnalysisResult>> AnalyzeBatchAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default);

    /// <summary>
    /// 触发自动分析（扫描完成后 MainViewModel 调用）。
    ///
    /// 前置条件（任一不满足直接返回空列表，不抛异常）：
    ///   1. config.AIEnabled == true
    ///   2. config.AIAutoAnalyze == true
    ///   3. config.UserMode == Expert
    ///   4. _aiAdvisorService.IsAvailable == true
    ///
    /// 筛选规则（委托给 IAiAdvisorService.AutoAnalyzeUncategorizedAsync）：
    ///   未分类 + >100MB + 无 AiLabel，最多 500 个。
    /// </summary>
    Task<IReadOnlyList<AiAnalysisResult>> TriggerAutoAnalyzeAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default);

    /// <summary>
    /// 取消当前正在进行的批量/自动分析。
    /// 已完成文件的结果保留，未开始的文件被跳过。
    /// </summary>
    void CancelCurrentAnalysis();

    // ============================================================
    // 状态查询
    // ============================================================

    /// <summary>是否有批量/自动分析正在进行中</summary>
    bool IsAnalyzing { get; }

    /// <summary>当前分析进度快照，无分析时为 null</summary>
    AiAnalysisProgress? CurrentProgress { get; }

    /// <summary>
    /// 进度变更事件（200ms 节流）。
    /// 批量分析和自动分析期间按文件粒度触发。
    /// </summary>
    event EventHandler<AiAnalysisProgress>? ProgressChanged;

    /// <summary>批量/自动分析完成事件</summary>
    event EventHandler<AiAnalysisCompletedEventArgs>? AnalysisCompleted;
}
```

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent.AI
```

- [ ] **Step 3: 提交**

```bash
git add src/DirectoryCleanAgent.AI/IAiAnalysisCoordinator.cs
git commit -m "feat: add IAiAnalysisCoordinator interface

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 实现 AiAnalysisCoordinator

**Files:**
- Create: `src/DirectoryCleanAgent.AI/AiAnalysisCoordinator.cs`

**Interfaces:**
- Consumes: `IAiAnalysisCoordinator` (Task 2), `AiAnalysisProgress`, `AiAnalysisCompletedEventArgs` (Task 1), `IAiAdvisorService`, `IConfigService`
- Produces: `AiAnalysisCoordinator` (class) — 供 Task 4 DI 注册

- [ ] **Step 1: 编写失败测试 — 去重逻辑**

```csharp
// src/DirectoryCleanAgent.Tests/AI/AiAnalysisCoordinatorTests.cs

using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.AI;

public class AiAnalysisCoordinatorTests
{
    [Fact]
    public async Task AnalyzeBatchAsync_SameFileTwice_OnlyCallsAdvisorOnce()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = true,
            AIAutoAnalyze = true,
            UserMode = UserMode.Expert
        });
        advisorMock.Setup(a => a.IsAvailable).Returns(true);
        advisorMock.Setup(a => a.AnalyzeFilesAsync(It.IsAny<IReadOnlyList<FileDecisionCache>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiAnalysisResult>
            {
                AiAnalysisResult.Success(@"C:\test\file1.tmp", "safe", 0.9, "test")
            });

        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        var cache = new FileDecisionCache
        {
            FilePath = @"C:\test\file1.tmp",
            SizeBytes = 200L * 1024 * 1024,
            SemanticCategory = "Uncategorized"
        };

        // Act
        await coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);
        await coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);

        // Assert — advisor 只被调用一次
        advisorMock.Verify(a => a.AnalyzeFilesAsync(
            It.IsAny<IReadOnlyList<FileDecisionCache>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TriggerAutoAnalyzeAsync_AiDisabled_ReturnsEmpty()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = false,  // AI 禁用
            AIAutoAnalyze = true,
            UserMode = UserMode.Expert
        });
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // Act
        var results = await coordinator.TriggerAutoAnalyzeAsync(
            Array.Empty<FileDecisionCache>(), CancellationToken.None);

        // Assert
        Assert.Empty(results);
        advisorMock.Verify(a => a.AutoAnalyzeUncategorizedAsync(
            It.IsAny<IReadOnlyList<FileDecisionCache>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TriggerAutoAnalyzeAsync_SimpleMode_ReturnsEmpty()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = true,
            AIAutoAnalyze = true,
            UserMode = UserMode.Simple  // 简单模式
        });
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // Act
        var results = await coordinator.TriggerAutoAnalyzeAsync(
            Array.Empty<FileDecisionCache>(), CancellationToken.None);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void CancelCurrentAnalysis_Requested_CancellationTokenIsCancelled()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig());
        advisorMock.Setup(a => a.IsAvailable).Returns(true);
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // 启动一个批量分析（在后台运行）
        var cache = new FileDecisionCache
        {
            FilePath = @"C:\test\file1.tmp",
            SizeBytes = 200L * 1024 * 1024
        };
        // 设置 advisor mock 延迟返回以模拟运行中
        advisorMock.Setup(a => a.AnalyzeFilesAsync(It.IsAny<IReadOnlyList<FileDecisionCache>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<FileDecisionCache> f, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
                return new List<AiAnalysisResult>();
            });

        var task = coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);

        // Act
        coordinator.CancelCurrentAnalysis();

        // Assert — 任务应该很快完成（被取消）
        var completed = task.Wait(2000);
        Assert.True(completed);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet test src/DirectoryCleanAgent.Tests --filter "FullyQualifiedName~AiAnalysisCoordinatorTests"
```
Expected: FAIL — `AiAnalysisCoordinator` 类不存在

- [ ] **Step 3: 实现 AiAnalysisCoordinator**

```csharp
// src/DirectoryCleanAgent.AI/AiAnalysisCoordinator.cs

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

        // 委托给 AI 服务执行筛选和分析
        var results = await _aiAdvisor.AutoAnalyzeUncategorizedAsync(allFiles, ct);
        return results;
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
        var completedCount = 0;
        var failedCount = 0;
        var results = new List<AiAnalysisResult>(totalCount);

        try
        {
            // 更新初始进度
            UpdateProgress(totalCount, 0, 0, files[0].FilePath, false);

            // 逐个调用 IAiAdvisorService.AnalyzeFileAsync（通过缓存文件路径）
            foreach (var cache in files)
            {
                batchCts.Token.ThrowIfCancellationRequested();

                try
                {
                    UpdateProgress(totalCount, completedCount, failedCount, cache.FilePath, false);

                    var result = await _aiAdvisor.AnalyzeFileAsync(cache.FilePath, batchCts.Token);

                    if (result?.IsSuccess == true)
                    {
                        completedCount++;
                        results.Add(result);
                        _fileStates[cache.FilePath] = AnalysisState.Completed;
                    }
                    else
                    {
                        failedCount++;
                        if (result != null)
                            results.Add(result);
                        _fileStates[cache.FilePath] = AnalysisState.Failed;
                    }
                }
                catch (OperationCanceledException)
                {
                    _fileStates[cache.FilePath] = AnalysisState.Failed;
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _fileStates[cache.FilePath] = AnalysisState.Failed;
                    _logger.LogWarning(ex, "批量分析中单文件失败: {Path}", cache.FilePath);
                    results.Add(AiAnalysisResult.Failure(cache.FilePath, ex.Message));
                }

                UpdateProgress(totalCount, completedCount, failedCount, null, false);
            }

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

            return results.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("批量 AI 分析被取消: 已完成={Completed}, 失败={Failed}",
                completedCount, failedCount);

            UpdateProgress(totalCount, completedCount, failedCount, null, true);

            OnAnalysisCompleted(new AiAnalysisCompletedEventArgs
            {
                SuccessCount = completedCount,
                FailedCount = failedCount,
                WasCancelled = true
            });

            return results.AsReadOnly();
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
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet test src/DirectoryCleanAgent.Tests --filter "FullyQualifiedName~AiAnalysisCoordinatorTests"
```
Expected: 4/4 PASS

- [ ] **Step 5: 提交**

```bash
git add src/DirectoryCleanAgent.AI/AiAnalysisCoordinator.cs src/DirectoryCleanAgent.Tests/AI/AiAnalysisCoordinatorTests.cs
git commit -m "feat: implement AiAnalysisCoordinator with dedup and progress throttling

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 注册 IAiAnalysisCoordinator 到 DI 容器

**Files:**
- Modify: `src/DirectoryCleanAgent.AI/AiServiceRegistration.cs`

**Interfaces:**
- Consumes: `AiAnalysisCoordinator` (Task 3), `IAiAdvisorService`, `IConfigService`
- Produces: DI 容器中可解析 `IAiAnalysisCoordinator`

- [ ] **Step 1: 在 AddAiLayer 末尾追加 Coordinator 注册**

定位到 `AiServiceRegistration.cs` 第 107 行附近（`IAiAdvisorService` 注册后的 `return services;` 之前），追加：

```csharp
// 7. IAiAnalysisCoordinator / AiAnalysisCoordinator — 协调器（单例）
services.AddSingleton<IAiAnalysisCoordinator>(sp =>
{
    var aiAdvisor = sp.GetRequiredService<IAiAdvisorService>();
    var configService = sp.GetRequiredService<IConfigService>();
    var logger = sp.GetRequiredService<ILogger<AiAnalysisCoordinator>>();

    return new AiAnalysisCoordinator(aiAdvisor, configService, logger);
});
```

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent.AI
```

- [ ] **Step 3: 提交**

```bash
git add src/DirectoryCleanAgent.AI/AiServiceRegistration.cs
git commit -m "feat: register IAiAnalysisCoordinator as singleton in DI

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 改造 FileListViewModel — 注入协调器 + 替换 stub + 批量入口

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs`
- Modify: `src/DirectoryCleanAgent.Tests/UI/FileListViewModelTests.cs`（若测试因构造函数变更而编译失败）

**Interfaces:**
- Consumes: `IAiAnalysisCoordinator` (Task 4), `AiAnalysisProgress`, `AiAnalysisCompletedEventArgs` (Task 1)
- Produces: `AnalyzeSelectedFilesCommand`, `CancelAiAnalysisCommand`, 进度/取消绑定属性

- [ ] **Step 1: 修改构造函数签名，新增 IAiAnalysisCoordinator 参数**

在 `FileListViewModel.cs` 的构造函数参数列表末尾（`IAppStateService appStateService` 之后），新增：

```csharp
IAiAnalysisCoordinator aiCoordinator)
```

在构造函数体末尾（`_logger.LogMethodEntry(...)` 之前），新增：

```csharp
_aiCoordinator = aiCoordinator ?? throw new ArgumentNullException(nameof(aiCoordinator));
```

在字段声明区域（`private bool _disposed;` 之前），新增：

```csharp
private readonly IAiAnalysisCoordinator _aiCoordinator;
```

- [ ] **Step 2: 新增批量分析和取消命令**

在构造函数中 `RequestAiAnalysisCommand = ...` 之后，追加：

```csharp
AnalyzeSelectedFilesCommand = new RelayCommand(async () => await ExecuteAnalyzeSelectedFilesAsync());
CancelAiAnalysisCommand = new RelayCommand(ExecuteCancelAiAnalysis);
```

在类顶部字段声明区域（`RequestAiAnalysisCommand` 之后），新增：

```csharp
public RelayCommand AnalyzeSelectedFilesCommand { get; }
public RelayCommand CancelAiAnalysisCommand { get; }
```

- [ ] **Step 3: 新增进度和取消绑定属性**

在字段声明区域追加：

```csharp
private string _aiProgressText = string.Empty;
private bool _isAiAnalyzing;
private bool _isAiAnalysisCancellable;
```

在属性区域追加：

```csharp
/// <summary>AI 分析进度文本（绑定到状态栏）</summary>
public string AiProgressText
{
    get => _aiProgressText;
    set => SetProperty(ref _aiProgressText, value);
}

/// <summary>是否有 AI 分析正在进行中（控制进度条和取消按钮可见性）</summary>
public bool IsAiAnalyzing
{
    get => _isAiAnalyzing;
    set => SetProperty(ref _isAiAnalyzing, value);
}

/// <summary>当前是否可取消 AI 分析</summary>
public bool IsAiAnalysisCancellable
{
    get => _isAiAnalysisCancellable;
    set => SetProperty(ref _isAiAnalysisCancellable, value);
}
```

- [ ] **Step 4: 替换 ExecuteRequestAiAnalysis stub 为真实实现**

用以下代码替换 `FileListViewModel.cs:1297-1309` 的 `ExecuteRequestAiAnalysis` 方法：

```csharp
private async void ExecuteRequestAiAnalysis(FileListItem? item)
{
    if (item == null) return;

    _logger.LogInformation("请求 AI 分析: {Path}", item.FullPath);

    // 设置 analyzing 状态
    item.AiLabel = "analyzing";
    item.UpdateAiDisplay();

    try
    {
        var result = await _aiCoordinator.AnalyzeSingleAsync(item.CacheKey);

        if (result?.IsSuccess == true)
        {
            item.AiLabel = result.Label;
            item.AiConfidence = result.Confidence;
            item.AiExplanation = result.Explanation;
            _logger.LogInformation("AI 单文件分析成功: {Path} → {Label} (置信度={Confidence:P})",
                item.FullPath, result.Label, result.Confidence);
        }
        else
        {
            // 分析失败或 AI 不可用 → 重置状态
            item.AiLabel = null;
            item.AiConfidence = null;
            item.AiExplanation = null;
            _logger.LogWarning("AI 单文件分析失败: {Path}, 错误={Error}",
                item.FullPath, result?.ErrorMessage ?? "服务不可用");
        }
    }
    catch (Exception ex)
    {
        item.AiLabel = null;
        _logger.LogError(ex, "AI 单文件分析异常: {Path}", item.FullPath);
    }
    finally
    {
        item.UpdateAiDisplay();
    }
}
```

- [ ] **Step 5: 实现批量分析命令**

在 `ExecuteToggleFileCheck` 方法之后，新增：

```csharp
private async Task ExecuteAnalyzeSelectedFilesAsync()
{
    try
    {
        // 收集所有勾选的文件
        var selectedItems = CurrentFileList.Where(f => f.IsChecked).ToList();
        if (selectedItems.Count == 0)
        {
            _logger.LogInformation("没有勾选的文件，跳过批量 AI 分析");
            return;
        }

        _logger.LogInformation("批量 AI 分析: 选中 {Count} 个文件", selectedItems.Count);

        // 从缓存中查找对应的 FileDecisionCache
        var cacheEntries = _allActionableCache
            .Where(c => selectedItems.Any(s => s.CacheKey == c.FilePath))
            .ToList();

        if (cacheEntries.Count == 0)
        {
            _logger.LogWarning("未在缓存中找到勾选文件的决策记录");
            return;
        }

        // 标记所有选中行为 analyzing 状态
        foreach (var item in selectedItems)
        {
            item.AiLabel = "analyzing";
            item.UpdateAiDisplay();
        }

        // 调用协调器批量分析
        var results = await _aiCoordinator.AnalyzeBatchAsync(cacheEntries, CancellationToken.None);

        // 结果回写到 UI 行
        foreach (var result in results)
        {
            var item = CurrentFileList.FirstOrDefault(f => f.CacheKey == result.FilePath);
            if (item != null)
            {
                item.AiLabel = result.Label;
                item.AiConfidence = result.Confidence;
                item.AiExplanation = result.Explanation;
                item.UpdateAiDisplay();
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "批量 AI 分析异常");
    }
}

private void ExecuteCancelAiAnalysis()
{
    _logger.LogMethodEntry("用户取消 AI 分析");
    _aiCoordinator.CancelCurrentAnalysis();
}
```

- [ ] **Step 6: 在构造函数中订阅协调器事件**

在构造函数末尾 `_logger.LogMethodEntry(...)` 之前，新增：

```csharp
// 订阅 AI 协调器进度和完成事件
_aiCoordinator.ProgressChanged += OnAiProgressChanged;
_aiCoordinator.AnalysisCompleted += OnAiAnalysisCompleted;
```

- [ ] **Step 7: 实现事件回调方法**

在 `OnAppStateChanged` 方法之前，新增：

```csharp
private void OnAiProgressChanged(object? sender, AiAnalysisProgress progress)
{
    RunOnUIThreadAsync(() =>
    {
        AiProgressText = $"AI 分析中… {progress.CompletedCount}/{progress.TotalCount}";
        IsAiAnalyzing = progress.CompletedCount < progress.TotalCount;
        IsAiAnalysisCancellable = !progress.IsCancelRequested;

        // 更新当前正在分析的行状态
        if (progress.CurrentFilePath != null)
        {
            var item = CurrentFileList.FirstOrDefault(f => f.CacheKey == progress.CurrentFilePath);
            if (item != null && item.AiLabel != "analyzing")
            {
                item.AiLabel = "analyzing";
                item.UpdateAiDisplay();
            }
        }
    });
}

private async void OnAiAnalysisCompleted(object? sender, AiAnalysisCompletedEventArgs e)
{
    await RunOnUIThreadAsync(() =>
    {
        IsAiAnalyzing = false;
        AiProgressText = e.WasCancelled
            ? "AI 分析已取消"
            : $"AI 分析完成：成功 {e.SuccessCount} 个"
              + (e.FailedCount > 0 ? $"，失败 {e.FailedCount} 个" : "");
    });

    // 刷新文件列表（AI 标签变更可能改变分组归属）
    _ = LoadDataAsync();
}
```

需要在文件顶部新增 using：

```csharp
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
```

- [ ] **Step 8: Dispose 中取消事件订阅**

在 `Dispose()` 方法的 `_appStateService.StateChanged -= OnAppStateChanged;` 之后，新增：

```csharp
_aiCoordinator.ProgressChanged -= OnAiProgressChanged;
_aiCoordinator.AnalysisCompleted -= OnAiAnalysisCompleted;
```

- [ ] **Step 9: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent
```

- [ ] **Step 10: 更新测试中的构造函数调用**

检查 `FileListViewModelTests.cs` 是否因构造函数新增参数而编译失败。若是，添加 `Mock.Of<IAiAnalysisCoordinator>()` 作为新参数。

```bash
dotnet build src/DirectoryCleanAgent.Tests
```

- [ ] **Step 11: 提交**

```bash
git add src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs
git add src/DirectoryCleanAgent.Tests/UI/FileListViewModelTests.cs
git commit -m "feat: connect FileListViewModel to IAiAnalysisCoordinator

Replace AI analysis stub with real coordinator calls.
Add batch analysis command, cancel command, and progress binding.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: 改造 MainViewModel — 扫描完成后自动触发 AI 分析

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `IAiAnalysisCoordinator` (Task 4), `IFileDecisionCacheRepository`（已有，来自 Data 层）
- Produces: 扫描完成后自动触发 AI 分析，进度/完成事件订阅，Toast 通知

- [ ] **Step 1: 查看 MainViewModel 构造函数以确定插入位置**

```bash
# 确认当前依赖注入列表
```

构造函数位于 `MainViewModel.cs:70-82`。需要在参数列表末尾新增两个依赖。

- [ ] **Step 2: 新增字段和构造函数参数**

在字段声明区域（`private SimulationResult? _cachedSimulationResult;` 附近）新增：

```csharp
private readonly IAiAnalysisCoordinator _aiCoordinator;
private readonly IFileDecisionCacheRepository _cacheRepo;  // 获取全量缓存用于自动分析
```

在构造函数参数列表末尾新增：

```csharp
IAiAnalysisCoordinator aiCoordinator,
IFileDecisionCacheRepository cacheRepo)
```

在构造函数体末尾新增：

```csharp
_aiCoordinator = aiCoordinator ?? throw new ArgumentNullException(nameof(aiCoordinator));
_cacheRepo = cacheRepo ?? throw new ArgumentNullException(nameof(cacheRepo));

// 订阅 AI 协调器事件
_aiCoordinator.ProgressChanged += OnAiProgressChanged;
_aiCoordinator.AnalysisCompleted += OnAiAnalysisCompleted;
```

- [ ] **Step 3: 新增进度和完成绑定属性**

在属性区域追加：

```csharp
private string _aiProgressText = string.Empty;
private bool _isAiAnalyzing;

/// <summary>AI 分析进度文本（绑定到状态栏）</summary>
public string AiProgressText
{
    get => _aiProgressText;
    set
    {
        if (_aiProgressText != value)
        {
            _aiProgressText = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>是否有 AI 分析正在进行中</summary>
public bool IsAiAnalyzing
{
    get => _isAiAnalyzing;
    set
    {
        if (_isAiAnalyzing != value)
        {
            _isAiAnalyzing = value;
            OnPropertyChanged();
        }
    }
}
```

- [ ] **Step 4: 实现事件回调 + Toast 通知**

在 `MainViewModel` 类中新增：

```csharp
private void OnAiProgressChanged(object? sender, AiAnalysisProgress progress)
{
    Application.Current?.Dispatcher.Invoke(() =>
    {
        AiProgressText = $"AI 分析中… {progress.CompletedCount}/{progress.TotalCount}";
        IsAiAnalyzing = progress.CompletedCount < progress.TotalCount;
    });
}

private void OnAiAnalysisCompleted(object? sender, AiAnalysisCompletedEventArgs e)
{
    Application.Current?.Dispatcher.Invoke(() =>
    {
        IsAiAnalyzing = false;
        AiProgressText = e.WasCancelled
            ? "AI 分析已取消"
            : $"AI 分析完成：成功 {e.SuccessCount} 个"
              + (e.FailedCount > 0 ? $"，失败 {e.FailedCount} 个" : "");
    });

    // 应用在后台/最小化时弹出 Windows Toast
    if (Application.Current?.MainWindow?.IsActive != true)
    {
        ShowAiAnalysisToast(e.SuccessCount, e.FailedCount, e.WasCancelled);
    }
}

private void ShowAiAnalysisToast(int successCount, int failedCount, bool wasCancelled)
{
    try
    {
        var message = wasCancelled
            ? "AI 分析已取消"
            : $"AI 分析完成：成功 {successCount} 个"
              + (failedCount > 0 ? $"，失败 {failedCount} 个" : "");

        // 使用 NotifyIcon 气球提示（项目已有托盘图标基础设施）
        // 实际发送由 App 层的 NotifyIcon 实例处理
        // 此处通过 App 静态方法间接调用
        App.ShowBalloonTip("AI 智能分析", message);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "显示 AI 分析 Toast 通知失败");
    }
}
```

- [ ] **Step 5: 在扫描完成后触发自动分析**

定位到 `ExecuteSimulate` 方法中 `_cachedSimulationResult = result;` 之后（约第 688 行），追加：

```csharp
// 静默触发自动 AI 分析（不阻塞主流程）
_ = Task.Run(async () =>
{
    try
    {
        var config = _configService.Current;
        if (config.AIAutoAnalyze && config.UserMode == UserMode.Expert)
        {
            var allCache = await _cacheRepo.GetAllAsync(CancellationToken.None);
            await _aiCoordinator.TriggerAutoAnalyzeAsync(allCache, CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "自动 AI 分析触发失败");
    }
});
```

- [ ] **Step 6: 在文件顶部添加 using**

```csharp
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Data;
```

- [ ] **Step 7: 在 App.xaml.cs 中新增静态方法（Toast 支持）**

定位到 `App.xaml.cs` 中 `ServiceProvider` 属性附近，新增：

```csharp
private static System.Windows.Forms.NotifyIcon? _notifyIcon;

public static void SetNotifyIcon(System.Windows.Forms.NotifyIcon notifyIcon)
{
    _notifyIcon = notifyIcon;
}

public static void ShowBalloonTip(string title, string message)
{
    _notifyIcon?.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);
}
```

- [ ] **Step 8: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent
```

- [ ] **Step 9: 提交**

```bash
git add src/DirectoryCleanAgent/ViewModels/MainViewModel.cs
git add src/DirectoryCleanAgent/App.xaml.cs
git commit -m "feat: add auto AI analysis trigger after scan in MainViewModel

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 更新 MainWindow.xaml — 工具栏按钮 + 右键菜单 + 状态栏

**Files:**
- Modify: `src/DirectoryCleanAgent/MainWindow.xaml`

**Interfaces:**
- Consumes: `FileListViewModel.AnalyzeSelectedFilesCommand`, `CancelAiAnalysisCommand`, `RequestAiAnalysisCommand`, `AiProgressText`, `IsAiAnalyzing`, `IsAiAnalysisCancellable`

- [ ] **Step 1: 在工具栏区域新增「AI 分析选中项」按钮和取消按钮**

在工具栏 `StackPanel` 或 `ToolBar` 区域（需先查看 XAML 确认工具栏结构），追加：

```xml
<!-- AI 分析工具栏按钮 -->
<Button Content="🤖 AI 分析选中项"
        Command="{Binding AnalyzeSelectedFilesCommand}"
        ToolTip="对勾选的文件执行 AI 安全分析"
        Width="130" Height="30" Margin="4,0"
        Visibility="{Binding IsAiAnalyzing, Converter={x:Static converters:InverseBoolToVisibilityConverter.Instance}}"
        />
<Button Content="取消分析"
        Command="{Binding CancelAiAnalysisCommand}"
        ToolTip="取消当前 AI 分析"
        Width="90" Height="30" Margin="4,0"
        Visibility="{Binding IsAiAnalysisCancellable, Converter={x:Static converters:BoolToVisibilityConverter.Instance}}"
        />
```

> 注：若项目未定义 `BoolToVisibilityConverter`，需要在 `App.xaml` 资源中注册或创建该转换器。

- [ ] **Step 2: 在 DataGrid 上添加 ContextMenu**

在 `DataGrid` 标签内部追加：

```xml
<DataGrid.ContextMenu>
    <ContextMenu>
        <MenuItem Header="🤖 AI 分析此文件"
                  Command="{Binding RequestAiAnalysisCommand}"
                  CommandParameter="{Binding PlacementTarget.SelectedItem,
                      RelativeSource={RelativeSource AncestorType=ContextMenu}}" />
        <Separator />
        <MenuItem Header="🤖 AI 分析所有勾选文件"
                  Command="{Binding AnalyzeSelectedFilesCommand}" />
    </ContextMenu>
</DataGrid.ContextMenu>
```

- [ ] **Step 3: 在底部状态栏新增 AI 分析进度区域**

在底部状态栏 `StatusBar` 或 `StackPanel` 区域追加：

```xml
<!-- AI 分析进度 -->
<StackPanel Orientation="Horizontal" Margin="10,0,0,0"
            Visibility="{Binding IsAiAnalyzing, Converter={x:Static converters:BoolToVisibilityConverter.Instance}}">
    <ProgressBar IsIndeterminate="False"
                 Minimum="0" Maximum="100"
                 Value="{Binding AiProgressPercentage}"
                 Width="120" Height="12"
                 Margin="0,0,6,0" />
    <TextBlock Text="{Binding AiProgressText}"
               Foreground="#0078D4" FontSize="12"
               VerticalAlignment="Center" />
</StackPanel>
```

> 注：若需要进度百分比绑定，在 `FileListViewModel` 中新增计算属性：
> ```csharp
> public double AiProgressPercentage =>
>     _aiCoordinator.CurrentProgress is { TotalCount: > 0 } p
>         ? (double)p.CompletedCount / p.TotalCount * 100
>         : 0;
> ```


- [ ] **Step 4: 验证编译**

```bash
dotnet build src/DirectoryCleanAgent
```

- [ ] **Step 5: 提交**

```bash
git add src/DirectoryCleanAgent/MainWindow.xaml
git commit -m "feat: add AI analysis toolbar button, context menu, and status bar progress

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 8: 端到端集成测试与修复

**Files:**
- 可能修改: Task 3-7 中的任意文件

- [ ] **Step 1: 运行全部 AI 相关单元测试**

```bash
dotnet test src/DirectoryCleanAgent.Tests --filter "FullyQualifiedName~AI"
```
Expected: ALL PASS

- [ ] **Step 2: 运行全部测试确保无回归**

```bash
dotnet test
```
Expected: ALL PASS（或有已知的可接受失败）

- [ ] **Step 3: 检查 AI 分析完成后的文件列表刷新路径**

确认 `FileListViewModel.OnAiAnalysisCompleted` → `LoadDataAsync()` → `RebuildGroupTreeAsync()` 链路正确：
- AI 标签变更 → `DecisionEngine.Arbitrate()` 重新计算 `FinalAction` → 分组树重新构建
- 手动验证：在 `LoadDataAsync` 中有 `_logger.LogInformation("文件列表数据加载完成...")` 日志

- [ ] **Step 4: 检查资源清理**

确认 `FileListViewModel.Dispose()` 和 `MainViewModel`（如果实现 IDisposable）中正确取消事件订阅。

- [ ] **Step 5: 提交最终修复**

```bash
git add -A
git commit -m "fix: integration fixes for AI analysis coordinator

Co-Authored-By: Claude <noreply@anthropic.com>"
```
