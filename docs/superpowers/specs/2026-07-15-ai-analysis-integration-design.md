# AI 分析功能完善 — 设计文档

日期: 2026-07-15 | 版本: V1.0

---

## 一、背景与目标

AI 顾问核心服务层（`DirectoryCleanAgent.AI`）已在 B7 阶段完整实现，但 UI 层未完全接入。存在 4 个关键缺口：

| # | 缺口 | 文件 | 影响 |
|---|------|------|------|
| 1 | 单文件 AI 分析按钮是 MessageBox 占位 | `FileListViewModel.cs:1297` | 用户无法对单个文件发起 AI 分析 |
| 2 | 扫描完成后无自动 AI 分析触发 | `MainViewModel.cs` | `AIAutoAnalyze` 配置完全无效 |
| 3 | 无批量选中 + AI 分析入口 | `FileListViewModel.cs` | `AnalyzeFilesAsync` 无 UI 调用方 |
| 4 | ViewModels 直接依赖 `IAiAdvisorService` | 多处 | 缺少协调层，进度/去重/取消难以统一管理 |

**目标**：通过引入 `IAiAnalysisCoordinator` 协调器中间层，打通全部 3 个 AI 分析入口，并提供统一进度反馈和取消支持。

---

## 二、架构设计

### 2.1 新增三层结构

```
┌─────────────────────────────────────────────────────────┐
│  ViewModels (FileListViewModel / MainViewModel)         │
│  只依赖 IAiAnalysisCoordinator，不直接接触 AI 服务       │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│  IAiAnalysisCoordinator (新增)                           │
│  ┌──────────────────────────────────────────────────┐   │
│  │ • 请求去重（同一文件不重复分析）                    │   │
│  │ • 进度聚合（Total/Completed/Failed + 当前文件路径） │   │
│  │ • 事件广播（ProgressChanged / AnalysisCompleted）  │   │
│  │ • 取消管理（CancelCurrentAnalysis）                │   │
│  │ • 通知策略（前台状态栏 vs 后台 Windows Toast）      │   │
│  └──────────────────────────────────────────────────┘   │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│  IAiAdvisorService (已有，不变)                          │
│  AnalyzeFileAsync / AnalyzeFilesAsync /                 │
│  AutoAnalyzeUncategorizedAsync / TestConnectionAsync    │
└─────────────────────────────────────────────────────────┘
```

### 2.2 生命周期

- `AiAnalysisCoordinator` 注册为 **单例**（DI 容器中与 `IAiAdvisorService` 同级）
- 进度状态在 ViewModel 之间共享：MainViewModel 触发自动分析时，FileListViewModel 也能看到进度更新
- 去重集合生命周期 = Coordinator 单例生命周期（应用重启后重置）

---

## 三、核心接口设计

### 3.1 `IAiAnalysisCoordinator`

```csharp
public interface IAiAnalysisCoordinator
{
    // ============================================================
    // 操作入口
    // ============================================================

    /// <summary>分析单个文件（UI 单文件"AI分析"按钮）</summary>
    /// <returns>分析结果；AI 不可用或已在分析中则返回 null</returns>
    Task<AiAnalysisResult?> AnalyzeSingleAsync(string filePath, CancellationToken ct = default);

    /// <summary>分析批量文件（工具栏按钮 / 右键菜单）</summary>
    /// <param name="files">待分析文件列表，内部自动去重 + 跳过已有标签的文件</param>
    Task<IReadOnlyList<AiAnalysisResult>> AnalyzeBatchAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default);

    /// <summary>触发自动分析（扫描完成后 MainViewModel 调用）</summary>
    /// <remarks>
    /// 前置条件（任一不满足直接返回空）：
    ///   1. AIEnabled == true
    ///   2. AIAutoAnalyze == true
    ///   3. UserMode == Expert
    ///   4. IAiAdvisorService.IsAvailable == true
    /// </remarks>
    Task<IReadOnlyList<AiAnalysisResult>> TriggerAutoAnalyzeAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default);

    /// <summary>取消当前正在进行的批量/自动分析</summary>
    void CancelCurrentAnalysis();

    // ============================================================
    // 状态查询
    // ============================================================

    bool IsAnalyzing { get; }
    AiAnalysisProgress? CurrentProgress { get; }

    event EventHandler<AiAnalysisProgress>? ProgressChanged;
    event EventHandler<AiAnalysisCompletedEventArgs>? AnalysisCompleted;
}
```

### 3.2 关键 DTO

```csharp
public sealed class AiAnalysisProgress
{
    /// <summary>总任务数</summary>
    public int TotalCount { get; init; }

    /// <summary>已完成任务数（含成功和失败），即 CompletedCount = 成功 + FailedCount</summary>
    public int CompletedCount { get; init; }

    /// <summary>失败任务数（是 CompletedCount 的子集）</summary>
    public int FailedCount { get; init; }

    /// <summary>正在处理的文件路径，无时为 null</summary>
    public string? CurrentFilePath { get; init; }

    /// <summary>是否已请求取消</summary>
    public bool IsCancelRequested { get; init; }

    /// <summary>便捷属性：成功完成数 = CompletedCount - FailedCount</summary>
    public int SucceededCount => CompletedCount - FailedCount;
}

public sealed class AiAnalysisCompletedEventArgs : EventArgs
{
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public bool WasCancelled { get; init; }
}
```

---

## 四、协调器实现关键细节

### 4.1 去重

- 使用 `ConcurrentDictionary<string, AnalysisState> _fileStates`
- `AnalysisState` 枚举：`Pending` / `Completed` / `Failed`
- 单次 Coordinator 生命周期内有效，应用重启后清空
- `AnalyzeBatchAsync` 和 `TriggerAutoAnalyzeAsync` 入口处过滤掉 `_fileStates` 中已 `Completed` 的文件
- 底层 `AiAdvisorService` 仍然会基于数据库 `AiLabel != null` 做二次去重（双重保障）

### 4.2 进度事件节流

- `ProgressChanged` 事件最小触发间隔：**200ms**
- 使用 `Stopwatch` 记录上次触发时间
- 节流期间累积的进度变更在下次触发时反映最新值

### 4.3 通知策略（AnalysisCompleted 事件中判断）

| 条件 | 通知方式 |
|------|----------|
| `MainWindow.IsActive == true` | 状态栏文本更新（通过 `ProgressChanged` 最后一帧渲染） |
| 应用最小化/后台 | `NotifyIcon.ShowBalloonTip()` 弹出 Windows 气泡通知 |

### 4.4 取消机制

- Coordinator 内部持有 `CancellationTokenSource? _batchCts`
- `CancelCurrentAnalysis()` → `_batchCts?.Cancel()`
- 底层的 `IAiAdvisorService.ProcessSingleFileAsync` 收到 `OperationCanceledException` 后中止后续文件
- 已完成的文件结果保留（不回滚）

### 4.5 自动分析前置条件

在 `TriggerAutoAnalyzeAsync` 内部按顺序检查：

```
_configService.Current.AIEnabled
  && _configService.Current.AIAutoAnalyze
  && _appStateService.CurrentUserMode == UserMode.Expert
  && _aiAdvisorService.IsAvailable
```

任一不满足 → 直接返回空列表，不抛异常，不触发事件。

---

## 五、ViewModel 改造

### 5.1 FileListViewModel

**新增 DI 依赖**：`IAiAnalysisCoordinator`（替换 stub 逻辑，不需要 `IAiAdvisorService` 直接注入）

**单文件分析改造**（替换现有 `ExecuteRequestAiAnalysis` stub）：

```csharp
// 行级"AI分析"按钮 → 设置 analyzing 状态 → 调用协调器 → 更新行 UI
private async void ExecuteRequestAiAnalysis(FileListItem? item)
{
    if (item == null) return;
    item.AiLabel = "analyzing";
    item.UpdateAiDisplay();

    var result = await _aiCoordinator.AnalyzeSingleAsync(item.CacheKey);
    if (result?.IsSuccess == true)
    {
        item.AiLabel = result.Label;
        item.AiConfidence = result.Confidence;
        item.AiExplanation = result.Explanation;
    }
    else
    {
        item.AiLabel = null; // 重置
    }
    item.UpdateAiDisplay();
}
```

**新增命令**：

```csharp
// 工具栏「AI 分析选中项」按钮 — 收集勾选行 → 查找 FileDecisionCache → 调用 AnalyzeBatchAsync
public RelayCommand AnalyzeSelectedFilesCommand { get; }

// 工具栏取消按钮
public RelayCommand CancelAiAnalysisCommand { get; }
```

**新增绑定属性**：

```csharp
private string _aiProgressText = string.Empty;
public string AiProgressText { get; set; }        // "AI 分析中… 5/50"

private bool _isAiAnalyzing;
public bool IsAiAnalyzing { get; set; }           // 控制进度条和取消按钮可见性

private bool _isAiAnalysisCancellable;
public bool IsAiAnalysisCancellable { get; set; } // 批量分析时可取消
```

**事件订阅**：

```csharp
// 构造函数中订阅
_aiCoordinator.ProgressChanged += OnAiProgressChanged;
_aiCoordinator.AnalysisCompleted += OnAiAnalysisCompleted;

// 进度回调 — 更新属性
private void OnAiProgressChanged(object? sender, AiAnalysisProgress progress)
{
    AiProgressText = $"AI 分析中… {progress.CompletedCount}/{progress.TotalCount}";
    IsAiAnalyzing = progress.CompletedCount < progress.TotalCount;
    IsAiAnalysisCancellable = !progress.IsCancelRequested;

    // 更新当前正在分析的行状态（如果该行在 CurrentFileList 中）
    if (progress.CurrentFilePath != null)
    {
        var item = CurrentFileList.FirstOrDefault(f => f.CacheKey == progress.CurrentFilePath);
        if (item != null)
        {
            item.AiLabel = "analyzing";
            item.UpdateAiDisplay();
        }
    }
}

// 完成回调 — 刷新视图（AI 标签变更可能改变 FinalAction 进而改变分组归属）
private void OnAiAnalysisCompleted(object? sender, AiAnalysisCompletedEventArgs e)
{
    IsAiAnalyzing = false;
    AiProgressText = $"AI 分析完成：成功 {e.SuccessCount} 个" +
                     (e.FailedCount > 0 ? $"，失败 {e.FailedCount} 个" : "");
    _ = LoadDataAsync(); // 刷新文件列表
}
```

### 5.2 MainViewModel

**新增 DI 依赖**：`IAiAnalysisCoordinator`

**扫描完成触发自动分析**（在 `ExecuteSimulate` 末尾，`_cachedSimulationResult = result` 之后）：

```csharp
// 静默后台自动分析（不 await）
if (config.AIAutoAnalyze && _appStateService.CurrentUserMode == UserMode.Expert)
{
    var allCache = await _cacheRepo.GetAllAsync(ct);
    _ = _aiCoordinator.TriggerAutoAnalyzeAsync(allCache, CancellationToken.None);
}
```

> 注：`_cacheRepo` 目前未注入到 MainViewModel。需要在构造函数中新增 `IFileDecisionCacheRepository` 依赖。

**新增绑定属性**（与 FileListViewModel 共享契约，以便状态栏统一绑定）：

```csharp
private string _aiProgressText = string.Empty;
public string AiProgressText { get; set; }

private bool _isAiAnalyzing;
public bool IsAiAnalyzing { get; set; }
```

**事件订阅**：

```csharp
// 构造函数中订阅
_aiCoordinator.ProgressChanged += OnAiProgressChanged;
_aiCoordinator.AnalysisCompleted += OnAiAnalysisCompleted;

private void OnAiProgressChanged(object? sender, AiAnalysisProgress progress)
{
    AiProgressText = $"AI 分析中… {progress.CompletedCount}/{progress.TotalCount}";
    IsAiAnalyzing = progress.CompletedCount < progress.TotalCount;
}

private void OnAiAnalysisCompleted(object? sender, AiAnalysisCompletedEventArgs e)
{
    IsAiAnalyzing = false;
    AiProgressText = $"AI 分析完成：成功 {e.SuccessCount} 个";

    // 应用不在前台时弹出 Windows Toast
    if (Application.Current?.MainWindow?.IsActive != true)
    {
        ShowAiAnalysisToast(e.SuccessCount, e.FailedCount, e.WasCancelled);
    }
}
```

### 5.3 右键菜单

在 `MainWindow.xaml` 的 DataGrid 上添加 `ContextMenu`：

```xml
<DataGrid.ContextMenu>
    <ContextMenu>
        <MenuItem Header="AI 分析此文件" Command="{Binding RequestAiAnalysisCommand}"
                  CommandParameter="{Binding PlacementTarget.SelectedItem,
                      RelativeSource={RelativeSource AncestorType=ContextMenu}}" />
        <Separator />
        <MenuItem Header="AI 分析所有勾选文件" Command="{Binding AnalyzeSelectedFilesCommand}" />
    </ContextMenu>
</DataGrid.ContextMenu>
```

---

## 六、文件变更清单

| 操作 | 文件 | 说明 |
|------|------|------|
| **新建** | `src/DirectoryCleanAgent.AI/IAiAnalysisCoordinator.cs` | 协调器接口 |
| **新建** | `src/DirectoryCleanAgent.AI/AiAnalysisCoordinator.cs` | 协调器实现 |
| **新建** | `src/DirectoryCleanAgent.AI/Models/AiAnalysisProgress.cs` | 进度 DTO |
| **修改** | `src/DirectoryCleanAgent.AI/AiServiceRegistration.cs` | 注册 `IAiAnalysisCoordinator` 单例 |
| **修改** | `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs` | 注入 Coordinator、替换 stub、新增命令和事件订阅 |
| **修改** | `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs` | 注入 Coordinator + `IFileDecisionCacheRepository`、新增自动触发、事件订阅 |
| **修改** | `src/DirectoryCleanAgent/MainWindow.xaml` | 工具栏加「AI 分析选中项」按钮、DataGrid 加右键菜单、底部状态栏加 AI 进度区域 |

---

## 七、测试策略

| 层 | 测试内容 | 类型 |
|----|----------|------|
| `AiAnalysisCoordinator` | 去重逻辑（相同文件不重复请求） | 单元测试 |
| `AiAnalysisCoordinator` | 进度事件节流（200ms 内不应有重复事件） | 单元测试 |
| `AiAnalysisCoordinator` | 取消流程：Cancel → 底层收到 OCE → 已完成的文件结果保留 | 单元测试 |
| `AiAnalysisCoordinator` | 前置条件检查：AI 禁用/简单模式/非自动 → 空结果 | 单元测试 |
| `FileListViewModel` | 单文件分析命令：analyzing 状态 → 成功刷新 → 失败重置 | 单元测试（Mock Coordinator） |
| `FileListViewModel` | 批量分析进度更新 → 完成刷新 | 单元测试 |
| `MainViewModel` | 扫描完成后自动触发条件检查 | 单元测试（Mock Coordinator） |
| 集成 | 端到端：扫描 → 自动分析 → 文件列表刷新 → UI 状态正确 | 集成测试 |

---

## 八、边界条件与错误处理

1. **AI 服务不可用时**：`AnalyzeSingleAsync` 返回 `null`，按钮无效果但不崩溃；`AnalyzeBatchAsync`/`TriggerAutoAnalyzeAsync` 返回空列表
2. **自动分析在扫描取消时**：扫描的 `CancellationToken` 已被取消，不触发自动分析（`ct.IsCancellationRequested` 前置检查）
3. **Coordinator 正在批量分析时再次触发批量分析**：返回错误或排队？**本次决定：返回错误提示"AI 分析进行中，请稍后重试"**，不做排队（V1 简化）
4. **Coordinator 正在批量分析时触发单文件分析**：单文件分析可以独立进行（`AnalyzeSingleAsync` 不使用批量 CTS），两者可并行
5. **进度事件订阅泄漏**：`FileListViewModel` 和 `MainViewModel` 在 `Dispose()` 中取消订阅

---

## 九、不做的事情（明确范围边界）

1. **不做**批量分析请求排队 — V1 只支持同时一个批量/自动分析运行
2. **不做**分析历史记录查询 — 结果仅存储在 `FileDecisionCache` 的 `AiLabel` 字段中
3. **不做**AI 费用估算与展示
4. **不做**AI 服务类型自动检测
5. **不做**Prompt 模板可配置化
