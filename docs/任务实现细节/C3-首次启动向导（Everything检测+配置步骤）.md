# C3-首次启动向导（Everything 检测 + 配置步骤）— 阶段性关键决策与细节控制文档

> 文档用途：供项目负责人复核与纠偏，不对最终用户展示。
> 生成日期：2026-06-23 | V3.7 | 状态：代码已完成，27项测试通过，待人工复核

---

## 一、 关键架构/设计决策记录

### 决策1：步骤视图生成方式 — 代码后台构建 vs 独立 UserControl 文件

1. **最终决策**：在 `SetupWizardWindow.xaml.cs` 中用代码（`BuildStepContent(stepIndex)`）动态构建每个步骤的 UI 元素树，而非创建 5 个独立的 `.xaml` UserControl 文件。
2. **核心权衡（Trade-off）**：
   - 牺牲了 XAML 可视化编辑能力和设计师可读性（所有 UI 逻辑在 C# 代码中）。
   - 换取了零文件膨胀（5 个步骤只需 1 个窗口文件）、无 XAML 绑定路径错误的编译期风险、以及更快的代码修改速度。
3. **被否决的替代方案**：
   - **5 个独立 UserControl 文件**：每个步骤一个 `.xaml + .cs`，共 10 个文件。在当前项目规模下过于碎片化，且各步骤 UI 简单（表单/文本），代码构建已足够清晰。
   - **XAML DataTemplate + VisualStateManager**：通过 Window.Resources 定义 5 个 DataTemplate 并用 DataTrigger 切换。复杂性高且难以在运行时绑定 Step0 的动态检测状态变更。

### 决策2：托盘图标实现 — Shell_NotifyIcon P/Invoke vs WinForms NotifyIcon

1. **最终决策**：使用原生 Win32 `Shell_NotifyIcon` P/Invoke（`TrayIconService.cs`），不使用 `System.Windows.Forms.NotifyIcon`。
2. **核心权衡（Trade-off）**：
   - 牺牲了 NotifyIcon 的便捷 API（托盘图标创建/菜单只需约 10 行代码），需要手写 200+ 行 P/Invoke 代码。
   - 换取了零 WinForms 依赖冲突：WPF 项目的 `UserControl`、`ProgressBar`、`Timer` 等类型不会与 WinForms 同名类型产生 CS0104 歧义错误，保持引用清洁。
3. **被否决的替代方案**：
   - **UseWindowsForms=true + NotifyIcon**：引发 5 个 CS0104 类型歧义编译错误（DashboardCard、WarningBanner、FileListView、FileListViewModel、SetupWizardWindow），需逐文件修改为完全限定名，污染范围大。
   - **H.NotifyIcon.Wpf 第三方库**：引入新的 NuGet 依赖，未在设计文档中声明，违反项目"零外部依赖"原则。

### 决策3：EverythingDependencyDetector 接口抽象 — IEverythingDetector

1. **最终决策**：创建 `IEverythingDetector` 接口，让 `EverythingDependencyDetector` 实现该接口，`SetupWizardViewModel` 通过接口依赖而非具体类。
2. **核心权衡（Trade-off）**：
   - 增加 1 个接口文件（`IEverythingDetector.cs`），略微增加了间接层。
   - 换取了完整的单元测试可模拟性：`Mock<IEverythingDetector>` 可控制 `DetectAsync` 和 `WaitForIndexAsync` 的返回值，无需依赖真实 Everything SDK。
3. **被否决的替代方案**：
   - **直接 Mock 具体类**：Moq 无法 Mock 非虚方法（`EverythingDependencyDetector` 的公共方法均非 virtual），且其构造函数依赖 `ILogger<EverythingDependencyDetector>`，Mock 复杂度相当于创建接口。

### 决策4：向导与主窗口的启动顺序 — App.xaml.cs 编排

1. **最终决策**：在 `App.OnStartup` 第 5 步中，检查 `UserConfig.IsFirstRun`。若为 true，先创建 `SetupWizardWindow`，监听其 ViewModel 的 `WizardCompleted`/`WizardSkipped` 事件，事件触发后再创建 `MainWindow`。
2. **核心权衡（Trade-off）**：
   - App.xaml.cs 启动流程增加约 80 行（向导编排 + 托盘管理），但控制了全局窗口生命周期。
   - 换取了"向导→主窗口"的无缝过渡，避免了同时存在两个主窗口的状态管理复杂性。
3. **被否决的替代方案**：
   - **在 MainWindow 内部嵌入向导（Page/Flyout 模式）**：MainWindow 需承载完全不同的两种 UI 模式（向导 vs 主界面），导致 ViewModel 职责混乱，且向导的 `Window` 级控件（如托盘图标）无法在 Page 内使用。

---

## 二、 隐含前提与关键假设清单（风险预警）

1. **[假设1]**：假设 `EverythingDependencyDetector.DetectAsync()` 在非 Windows 平台或 Everything 未安装环境下的 `DllNotFoundException` 能正常捕获。当前已通过 try-catch 处理，但实际未在非 Windows 环境测试。（风险等级：低）
2. **[假设2]**：假设 `Shell_NotifyIcon` 在所有 Windows 10 1809+ 版本上行为一致。已知某些精简版 Windows 可能移除了 Explorer Shell 通知区域支持，此时托盘图标将不可见但不会导致崩溃。（风险等级：低）
3. **[假设3]**：假设后台等待模式下，用户不会在索引完成前强制退出。当前 `SetupWizardWindow_Closing` 事件处理了强制关闭（保存默认配置并进入主界面），但未测试此路径的长期运行稳定性。（风险等级：中）
4. **[假设4]**：假设 AI 连接测试可简化为模拟延迟（1.5 秒 Task.Delay），实际 HTTP 调用的超时、SSL 证书错误、防火墙拦截等异常路径待 B7（AI 顾问模块）完成后替换真实调用。（风险等级：高）
5. **[假设5]**：假设首次运行判断仅依赖 `UserConfig.IsFirstRun`（默认 true）。若用户删除配置文件后重启应用，将再次弹出向导。这是预期行为（见设计文档：配置丢失时应重新引导），但未对"配置文件存在但 isFirstRun 字段缺失"的 JSON 兼容场景做额外处理（System.Text.Json 默认为 false，会导致直接跳过向导）。（风险等级：中）

---

## 三、 边界与范围界定（明确"不做什么"）

1. **[明确不做]**：C3 阶段不实现设置页面（C4 任务）—— 向导中的配置项仅为初始设置，后续可通过设置页面修改。
2. **[明确不做]**：C3 阶段不实现完整的 Windows 通知中心交互（通知点击"查看结果"按钮唤回界面）—— 当前仅通过 `Shell_NotifyIcon` 显示气泡通知，点击通知的回调依赖 `NOTIFYICONDATA.dwInfoFlags` 和 `NIN_BALLOONUSERCLICK` 消息，此部分在 C3 中未实现（需在 C10 托盘任务中完善）。
3. **[明确不做]**：C3 阶段不实现 Docker/WSL 的自动检测逻辑—— Step3 仅显示硬编码的"自动保护目录"列表，实际检测依赖 B1（Everything 适配器）扫描结果。
4. **[明确不做]**：C3 阶段不实现组件化步骤加载（如 MEF/Plugin 动态加载步骤）—— 当前步骤在 `BuildSteps(UserMode)` 中硬编码构建，未来若需扩展步骤只需修改该方法。
5. **[明确不做]**：C3 阶段不做向导 UI 的本地化适配—— 所有 UI 文本为中文硬编码，本地化改造待 A6（本地化基础设施）与 C6 统一处理。
6. **[明确不做]**：C3 阶段不做深色主题下的向导窗口样式适配，当前默认使用浅色主题。

---

## 四、 待确认事项（需要人工介入的模糊点）

1. **[待确认A]**：向导 Step0 中 Everything 检测失败时的"退出"按钮调用 `Application.Current.Shutdown()`。若用户通过此路径退出，`IsFirstRun` 保持 true，下次启动仍进入向导。这是否符合预期？或者应该在退出前也设为 false？
2. **[待确认B]**：用户画像（Step2）的职业和设备用途字段为自由文本 ComboBox（可输入自定义值）。当前保存到 `UserConfig.UserProfession`/`DeviceUsage` 字符串字段。若未来需要统计分析，应考虑改为预定义枚举。
3. **[待确认C]**：AI 连接测试（Step4）当前为模拟实现（1.5 秒延迟后显示"通过"）。实际测试逻辑应在 B7 模块就绪后替换为真实的 `HttpClient` 调用。当前模拟代码是否需要在 B7 完成前先标记 `[Obsolete]` 或 TODO 注释？
4. **[待确认D]**：后台等待模式中，索引完成后自动触发 `WizardCompleted` 事件 → 关闭向导 → 显示主窗口。这会在用户没有主动操作的情况下切换窗口。若用户当时正在进行其他操作（如阅读浏览器），此行为可能造成干扰。是否应仅通知而不自动切换？
5. **[待确认E]**：Step3 中手动添加的排除目录存储在 `UserConfig.UserExcludedDirs`。当前 UI 不显示已添加的排除目录列表（仅一个输入框+添加按钮），用户无法查看或移除已添加项。是否需要在 Step3 中增加列表显示和删除功能？

---

## 五、 关键外部依赖锁定

- **依赖项1**：`DirectoryCleanAgent.Everything` 项目（A2 已实现） — `IEverythingDetector` / `EverythingDependencyDetector` / `EverythingDetectionResult`。若 A2 修改接口签名，C3 的 `SetupWizardViewModel` 构造函数需同步修改。
- **依赖项2**：`DirectoryCleanAgent.Core` 项目（A4 已实现） — `IConfigService` / `UserConfig` / `UserMode` / `ThemeType` 等枚举和配置模型。`IsFirstRun` 字段是 C3 新增的，需确保 A4 的 `JsonConfigService` 能正确序列化/反序列化该字段。
- **依赖项3**：`DirectoryCleanAgent` 主项目（C1 已实现） — `IAppStateService` / `AppStateService` / `ViewModelBase` / `RelayCommand`。C1 的 MVVM 基础设施被 C3 完全复用。
- **依赖项4**：Everything SDK ≥ 1.4.1.1000 — 运行时依赖。构建时不依赖，但 `EverythingNative` 互操作类（A2 已实现）需 Everything64.dll 在运行时可发现。
- **依赖项5**：`Moq` / `xUnit` NuGet 包 — 测试依赖，已缓存至本地 NuGet 源，离线可用。

---

## 六、 代码文件清单

### 新建文件（8个）

| 文件路径 | 行数 | 说明 |
|---------|------|------|
| `src/DirectoryCleanAgent/Models/WizardStepInfo.cs` | 24 | 向导步骤描述模型 |
| `src/DirectoryCleanAgent/ViewModels/SetupWizardViewModel.cs` | 520 | 向导核心 ViewModel |
| `src/DirectoryCleanAgent/Views/SetupWizardWindow.xaml` | 110 | 向导主窗口布局（步骤指示器+内容区+导航按钮） |
| `src/DirectoryCleanAgent/Views/SetupWizardWindow.xaml.cs` | 780 | 向导窗口代码后台（5 个步骤 UI 动态构建） |
| `src/DirectoryCleanAgent/Services/TrayIconService.cs` | 260 | 原生 Shell_NotifyIcon 托盘图标服务 |
| `src/DirectoryCleanAgent/Converters/InverseBoolToVisibilityConverter.cs` | 28 | 布尔取反到 Visibility 转换器 |
| `src/DirectoryCleanAgent.Everything/IEverythingDetector.cs` | 14 | Everything 检测器接口（解耦测试） |
| `src/DirectoryCleanAgent.Tests/UI/SetupWizardViewModelTests.cs` | 380 | 向导 ViewModel 单元测试（27 个用例） |

### 修改文件（5个）

| 文件路径 | 变更 |
|---------|------|
| `src/DirectoryCleanAgent/App.xaml.cs` | DI 注册 C3 服务 + 首次运行检测 + 向导→主窗口编排 + 托盘图标钩子 |
| `src/DirectoryCleanAgent.Core/Config/UserConfig.cs` | 新增 `IsFirstRun` 属性（默认 true） |
| `src/DirectoryCleanAgent.Everything/EverythingDependencyDetector.cs` | 实现 `IEverythingDetector` 接口 |
| `src/DirectoryCleanAgent/Converters/BoolToVisibilityConverter.cs` | 新增静态 `Instance` 单例 |
| `src/DirectoryCleanAgent/DirectoryCleanAgent.csproj` | 无需修改（依赖已完备） |

### 测试结果

**27/27 通过，0 失败，0 跳过。**
