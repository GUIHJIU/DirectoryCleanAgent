# C8-进度与取消UI集成 — 阶段性关键决策与细节控制文档

---

## 一、 关键架构/设计决策记录（必须包含"被否决的方案"）

### 决策 1: QuickClean/SmartSelect 接入方式

**最终决策**：在 `MainViewModel` 中直接注入 `IOperationExecutor`、`IDecisionEngine`、`IFileListProvider`、`IRuleEngine` 四个接口，通过新增的 `CollectCandidatesAsync` 私有方法串接 B1→B2→B3→B4 管道。

**核心权衡（Trade-off）**：
- 牺牲了 ViewModel 的"薄"特性（构造器从 9 参数增至 13 参数），换取了管道直接调用的低延迟和精确的进度控制能力。
- ViewModel 承担了协调职责而非纯粹的绑定职责，但避免了引入额外的编排服务层。

**被否决的替代方案**：
1. **新增 `ICleanupOrchestrator` 编排服务**：将 B1→B2→B3→B4 管道封装为一个独立服务，ViewModel 仅调用该服务。**放弃原因**：C8 任务要求状态栏进度百分比与取消按钮直接联动 CTS，引入中间层会导致取消令牌传递链延长，取消响应延迟增加一个 async 往返。
2. **保留 Mock 提供者，仅做 UI 增强**：继续使用 `IDashboardDataProvider.SimulateCleanOperationAsync()`，仅在 UI 层加进度百分比显示。**放弃原因**：Mock 数据不反映真实磁盘状态，进度值完全虚构，达不到"进度与取消 UI 集成"的实际验收标准。

### 决策 2: 进度条 IsIndeterminate 绑定修复方式

**最终决策**：在 `OperationProgress` 结构体上新增 `IsIndeterminate` 计算属性（`TotalCount <= 0`），XAML 直接绑定该布尔属性。

**核心权衡（Trade-off）**：
- 增加了 DTO 层的 UI 关注点（DTO 原本应保持纯数据结构），换取了 XAML 绑定的简洁性与正确性。

**被否决的替代方案**：
1. **新建 `PercentageToIndeterminateConverter` 转换器**：用 `IValueConverter` 将 `double` 百分比转为 `bool`。**放弃原因**：转换器属于纯 UI 层方案，但需要在多个 XAML 文件中重复注册；而结构体属性方案一次定义，随处可用，且语义更清晰（`IsIndeterminate` 自文档化）。

### 决策 3: 取消操作的 UI 状态反馈策略

**最终决策**：点击取消按钮后**立即**设置 `OperationStatusText = "正在取消..."`，然后调用 `_currentCts?.Cancel()`。

**核心权衡（Trade-off）**：
- "立即更新 UI"提供了即时反馈（用户知道自己点击已被接收），但若取消操作最终因异常失败，用户看到的"正在取消..."可能是误导性的。实际中 `CancellationTokenSource.Cancel()` 不会抛出异常，风险可控。

**被否决的替代方案**：
1. **仅在 finally 块中统一展示取消状态**：等 `OperationCanceledException` 被 catch 后再更新 UI。**放弃原因**：用户点击取消按钮到实际捕获异常之间可能有数百毫秒延迟（取决于当前文件操作耗时），无即时反馈会让用户误以为取消按钮无效从而重复点击。

---

## 二、 隐含前提与关键假设清单（风险预警）

1. **[假设 1]**：假设 DI 容器中 `IOperationExecutor`、`IDecisionEngine`、`IFileListProvider`、`IRuleEngine` 均已正确注册为 Singleton 且无循环依赖。
   - **风险**：若注册缺失或生命周期不一致，应用启动时 `MainViewModel` 解析将抛出 `InvalidOperationException`。
   - **验证方式**：`dotnet build` + 启动应用可即时发现。

2. **[假设 2]**：假设 `Everything` 服务在用户点击"一键清理/智能选择"时已处于运行状态且索引已就绪。
   - **风险**：若 Everything 未运行，`IFileListProvider.EnumerateFilesAsync` 将阻塞或返回空流，导致 `CollectCandidatesAsync` 无结果。
   - **缓解**：`CanQuickClean`/`CanSmartSelect` 的 CanExecute 守卫已检查 `IsIndexReady`。

3. **[假设 3]**：假设 `IDecisionEngine.Arbitrate()` 在无 AI 标签时（`AiLabel=null`）能正确降级为纯规则仲裁，不抛异常。
   - **风险**：若 Arbitrate 对 null `AiLabel` 无防护，管道将在 B3 阶段中断。
   - **验证方式**：代码审查 `DecisionEngine.Arbitrate` 实现。

4. **[假设 4]**：假设 `OperationExecutor.ExecuteAsync` 内部已正确处理 `.cleaning.tmp` 临时文件在取消时的清理。
   - **风险**：若取消发生在跨卷复制过程中而清理逻辑有 bug，将残留 `.cleaning.tmp` 文件。
   - **验证方式**：需集成测试验证（不在 C8 范围内）。

---

## 三、 边界与范围界定（明确"不做什么"）

1. **[明确不做]**：本期**不修改** `ExecuteSimulate` 和 `ExecuteExport` 的进度报告逻辑使其也更新 `StatusInfo`（它们仅更新 `OperationProgress` 属性，不更新状态文本）。这两个命令本身已通过独立的 `Progress<SimulationProgress>` 适配器工作。

2. **[明确不做]**：本期**不删除** `IDashboardDataProvider.SimulateCleanOperationAsync` 方法和 `MockDashboardDataProvider` 中的对应实现。虽然 `RunOperationAsync` 已被删除，但保留接口方法供未来可能的离线演示或自动化测试使用。

3. **[明确不做]**：本期**不处理** `ExecuteRefresh` 的真实管道接入（刷新扫描仍使用 Mock 数据 + 10 步模拟进度），此项为 C9+ 任务范围。

4. **[明确不做]**：本期**不实现**管道执行过程中的断点续传或进度持久化（如应用崩溃后恢复进度）。`CancellationToken` 取消后已处理的文件不回滚（设计文档规定），未处理的文件在下次操作中重新扫描。

5. **[明确不做]**：本期**不修改** `MockDataProviderTests` 中引用 `SimulateCleanOperationAsync` 的测试用例（该方法仍存在于接口中，测试仍有效）。

---

## 四、 待确认事项（需要人工介入的模糊点）

1. **[待确认项 A]**：`EverythingQueryParams` 中默认卷为 `"C:"`，若用户系统盘为 D: 或其他盘符，扫描将无结果。当前从 `_configService.Current.IncludedVolumes` 读取卷列表，若配置为空则 fallback 到 C:。**需确认**：安装向导（C3）是否保证 `IncludedVolumes` ≥ 1 项。

2. **[待确认项 B]**：一键清理的容量检查弹窗文案中"超出部分将直接永久删除"为用户提示，但实际代码中并未将 `DeleteMethod` 从 `RecycleBin` 切换为 `Permanent`——所有文件统一用 `DeleteMethod.RecycleBin`。**需确认**：是否需要根据容量情况动态切换删除方式，还是文案描述已足够。

3. **[待确认项 C]**：进度条 `IsIndeterminate` 的视觉样式依赖 `StatusBarProgressBarStyle` 中是否正确设置了 `IsIndeterminate` 时的 Marquee 动画。**需确认**：`CommonStyles.xaml` 中 `StatusBarProgressBarStyle` 是否包含 `IsIndeterminate` 的视觉状态定义。

4. **[待确认项 D]**：`CollectCandidatesAsync` 中每 100 个文件报告一次进度的频率是在无实测数据下的估计值。**需确认**：在 2M 文件规模下此频率是否会导致 UI 线程过载（每 100 个文件一次 `Dispatcher.Invoke`）。

---

## 五、 关键外部依赖锁定

| 依赖项 | 影响模块 | 若延期则 |
|--------|---------|---------|
| `IOperationExecutor`（B4 操作执行器） | `ExecuteQuickClean` / `ExecuteSmartSelect` 的删除阶段 | 一键清理和智能选择只能走到快照生成，无法实际执行删除 |
| `IDecisionEngine`（B3 决策引擎） | `CollectCandidatesAsync` 的仲裁环节 | 管道在 B3 阶段中断，无法区分 AutoDelete/SuggestDelete |
| `IFileListProvider`（B1 Everything 适配器） | `CollectCandidatesAsync` 的流式扫描 | 无法获取文件列表，管道入口即阻塞 |
| `IRuleEngine`（B2 规则引擎） | `CollectCandidatesAsync` 的规则评估 | 所有文件被判为 Keep，候选列表为空 |
| `Everything SDK ≥ 1.4.1.1000` | 整个 B1 扫描管道 | 应用启动即被拦截（设计文档规定 Everything 为强制依赖） |
| `IQuarantineManager.GetQuarantineStatsAsync` | 操作后状态栏隔离区计数刷新 | 非关键路径，失败仅影响状态栏隔离区数字显示 |
