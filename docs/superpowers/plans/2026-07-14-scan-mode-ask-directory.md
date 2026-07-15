# ScanMode "每次询问目录" 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `ScanMode.AskDirectoryEveryTime` 接入运行时扫描流程，点击"刷新扫描"时弹出目录选择对话框。

**Architecture:** 抽取 `IDirectoryPickerService` 接口（封装 `FolderBrowserDialog`），注入 `MainViewModel`，在 `ExecuteRefresh()` 中检查 `ScanMode` 决定是否弹出目录选择框。

**Tech Stack:** C# .NET 8, WPF, System.Windows.Forms, Moq, xUnit

## Global Constraints

- 不引入新的 NuGet 包
- 路径统一使用 `PathNormalizer.Normalize()` 转为 `\\?\` 格式
- 本地化字符串通过 `ILocalizationService.GetString()` 获取
- 遵循项目 DI 模式：接口和实现同在 `src/DirectoryCleanAgent/Services/`
- 生命周期：Transient

---

### Task 1: 创建 IDirectoryPickerService 接口

**Files:**
- Create: `src/DirectoryCleanAgent/Services/IDirectoryPickerService.cs`

**Interfaces:**
- Produces: `IDirectoryPickerService.PickDirectory(string description)` → `string?`

- [ ] **Step 1: 写入接口文件**

```csharp
namespace DirectoryCleanAgent.Services;

/// <summary>
/// 目录选择器——封装文件夹浏览对话框，供扫描模式使用。
/// </summary>
public interface IDirectoryPickerService
{
    /// <summary>
    /// 弹出目录选择对话框，返回用户选中的目录绝对路径。
    /// 用户取消时返回 null。
    /// </summary>
    /// <param name="description">对话框标题栏提示文本（应已本地化）</param>
    /// <returns>用户选中的目录绝对路径（普通格式），取消返回 null</returns>
    string? PickDirectory(string description);
}
```

- [ ] **Step 2: 提交**

```bash
git add src/DirectoryCleanAgent/Services/IDirectoryPickerService.cs
git commit -m "feat: 添加 IDirectoryPickerService 接口"
```

---

### Task 2: 创建 DirectoryPickerService 实现

**Files:**
- Create: `src/DirectoryCleanAgent/Services/DirectoryPickerService.cs`

**Interfaces:**
- Consumes: `IDirectoryPickerService` — 接口（Task 1）
- Produces: `DirectoryPickerService.PickDirectory(string description)` → `string?`

- [ ] **Step 1: 写入实现文件**

```csharp
using System.Windows.Forms;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 目录选择器实现——使用 WinForms FolderBrowserDialog 弹出目录选择对话框。
/// </summary>
public sealed class DirectoryPickerService : IDirectoryPickerService
{
    /// <inheritdoc />
    public string? PickDirectory(string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        return dialog.ShowDialog() == DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add src/DirectoryCleanAgent/Services/DirectoryPickerService.cs
git commit -m "feat: 实现 DirectoryPickerService（FolderBrowserDialog 封装）"
```

---

### Task 3: 添加本地化字符串

**Files:**
- Modify: `src/DirectoryCleanAgent.Core/Localization/Strings.resx` — 在 `Settings.ScanMode.AskDirectory` 条目后新增
- Modify: `src/DirectoryCleanAgent.Core/Localization/Strings.en.resx` — 在 `Settings.ScanMode.AskDirectory` 条目后新增

**Interfaces:**
- Produces: `ILocalizationService.GetString("Main.Scan.SelectDirectory")` → `"请选择要扫描的目录:"` (zh-CN) / `"Select directory to scan:"` (en-US)

- [ ] **Step 1: 在 Strings.resx 中添加简体中文条目**

在 `</data>` 关闭标签（`Settings.ScanMode.AskDirectory` 条目）之后插入：

```xml
  <data name="Main.Scan.SelectDirectory" xml:space="preserve">
    <value>请选择要扫描的目录:</value>
  </data>
```

修改位置：`src/DirectoryCleanAgent.Core/Localization/Strings.resx` 第 325 行之后（`</data>` 之后，`<!-- 设置 — AI 服务类型选项 -->` 注释之前）

- [ ] **Step 2: 在 Strings.en.resx 中添加英文条目**

在 `</data>` 关闭标签（`Settings.ScanMode.AskDirectory` 条目）之后插入：

```xml
  <data name="Main.Scan.SelectDirectory" xml:space="preserve">
    <value>Select directory to scan:</value>
  </data>
```

修改位置：`src/DirectoryCleanAgent.Core/Localization/Strings.en.resx` 第 271 行之后（`</data>` 之后，`<!-- Settings - AI Service Type Options -->` 注释之前）

- [ ] **Step 3: 提交**

```bash
git add src/DirectoryCleanAgent.Core/Localization/Strings.resx src/DirectoryCleanAgent.Core/Localization/Strings.en.resx
git commit -m "feat: 添加目录选择对话框本地化字符串 Main.Scan.SelectDirectory"
```

---

### Task 4: DI 注册

**Files:**
- Modify: `src/DirectoryCleanAgent/App.xaml.cs` — `ConfigureServices()` 方法

**Interfaces:**
- Consumes: `IDirectoryPickerService` / `DirectoryPickerService`（Task 1, Task 2）

- [ ] **Step 1: 添加 Transient 注册**

在 `src/DirectoryCleanAgent/App.xaml.cs` 的 `ConfigureServices()` 方法中，第 684 行（`// ---- C3: 首次启动向导 ----` 注释之上）插入：

```csharp
        // ---- ScanMode: 目录选择器 ----
        services.AddTransient<IDirectoryPickerService, DirectoryPickerService>();

```

- [ ] **Step 2: 提交**

```bash
git add src/DirectoryCleanAgent/App.xaml.cs
git commit -m "feat: 注册 IDirectoryPickerService 到 DI 容器"
```

---

### Task 5: 修改 MainViewModel

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs` — 构造函数 + ExecuteRefresh()

**Interfaces:**
- Consumes: `IDirectoryPickerService`（Task 1）、`PathNormalizer.Normalize()`（已有静态方法）

- [ ] **Step 1: 添加 private readonly 字段**

在 `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs` 第 39 行之后（`_ruleEngine` 字段之后）插入：

```csharp
    private readonly IDirectoryPickerService _directoryPicker;  // ScanMode: 目录选择器
```

- [ ] **Step 2: 添加 using 语句**

在文件顶部 `using DirectoryCleanAgent.Core.Localization;` 之后插入：

```csharp
using DirectoryCleanAgent.Core.PathHandling;
```

- [ ] **Step 3: 修改构造函数签名**

将原构造函数签名（第 54-67 行）：

```csharp
    public MainViewModel(
        ILogger<MainViewModel> logger,
        IDashboardDataProvider dataProvider,
        IAppStateService appStateService,
        ILocalizationService localization,
        ISimulationService simulationService,
        IReportExporter reportExporter,
        IConfigService configService,
        IQuarantineManager quarantineManager,
        IOperationExecutor operationExecutor,
        IDecisionEngine decisionEngine,
        IFileListProvider fileListProvider,
        IRuleEngine ruleEngine,
        FileListViewModel fileListViewModel)
```

替换为（在 `IRuleEngine ruleEngine` 之后新增 `IDirectoryPickerService directoryPicker` 参数）：

```csharp
    public MainViewModel(
        ILogger<MainViewModel> logger,
        IDashboardDataProvider dataProvider,
        IAppStateService appStateService,
        ILocalizationService localization,
        ISimulationService simulationService,
        IReportExporter reportExporter,
        IConfigService configService,
        IQuarantineManager quarantineManager,
        IOperationExecutor operationExecutor,
        IDecisionEngine decisionEngine,
        IFileListProvider fileListProvider,
        IRuleEngine ruleEngine,
        IDirectoryPickerService directoryPicker,
        FileListViewModel fileListViewModel)
```

- [ ] **Step 4: 在构造函数体中初始化新字段**

在第 80 行（`_ruleEngine = ruleEngine ?? ...`）之后插入：

```csharp
        _directoryPicker = directoryPicker ?? throw new ArgumentNullException(nameof(directoryPicker));
```

- [ ] **Step 5: 修改 ExecuteRefresh() 方法**

在 `ExecuteRefresh()` 方法中，第 873 行（`var config = _configService.Current;`）之后、第 874 行（`var queryParams = new EverythingQueryParams`）之前插入 ScanMode 判断逻辑。

完整替换片段（原第 873-880 行）：

原代码：
```csharp
            var config = _configService.Current;
            var queryParams = new EverythingQueryParams
            {
                Volumes = config.IncludedVolumes.Count > 0
                    ? config.IncludedVolumes
                    : new List<string> { "C:" },
                MaxResults = config.MaxScanFiles > 0 ? config.MaxScanFiles : null
            };
```

替换为：
```csharp
            var config = _configService.Current;

            // ScanMode: 每次询问目录 → 弹出目录选择对话框
            string? pickedPath = null;
            if (config.ScanMode == ScanMode.AskDirectoryEveryTime)
            {
                pickedPath = _directoryPicker.PickDirectory(
                    _localization.GetString("Main.Scan.SelectDirectory"));
                if (pickedPath == null)
                {
                    _logger.LogDebug("用户取消了目录选择，扫描未启动");
                    return;
                }
                _logger.LogInformation("用户选择了扫描目录: {Path}", pickedPath);
            }

            var queryParams = new EverythingQueryParams
            {
                Volumes = config.IncludedVolumes.Count > 0
                    ? config.IncludedVolumes
                    : new List<string> { "C:" },
                MaxResults = config.MaxScanFiles > 0 ? config.MaxScanFiles : null
            };

            // ScanMode: 目录模式下使用 PathFilter 精确限定扫描范围
            if (config.ScanMode == ScanMode.AskDirectoryEveryTime && pickedPath != null)
            {
                queryParams = queryParams with
                {
                    PathFilter = PathNormalizer.Normalize(pickedPath)
                };
            }
```

- [ ] **Step 6: 提交**

```bash
git add src/DirectoryCleanAgent/ViewModels/MainViewModel.cs
git commit -m "feat: MainViewModel 接入 ScanMode.AskDirectoryEveryTime 目录选择逻辑"
```

---

### Task 6: 更新 MainViewModelTests 构造函数

**Files:**
- Modify: `src/DirectoryCleanAgent.Tests/UI/MainViewModelTests.cs` — 构造函数

**Interfaces:**
- Consumes: `IDirectoryPickerService` mock（Task 1）

- [ ] **Step 1: 添加 Mock 字段**

在测试类字段声明区域（第 33 行 `_ruleEngineMock` 之后）插入：

```csharp
    private readonly Mock<IDirectoryPickerService> _directoryPickerMock;
```

- [ ] **Step 2: 初始化 Mock**

在构造函数中（第 50 行 `_ruleEngineMock = new Mock<IRuleEngine>();` 之后）插入：

```csharp
        _directoryPickerMock = new Mock<IDirectoryPickerService>();
```

- [ ] **Step 3: 修改 MainViewModel 构造调用**

将构造函数调用（第 100-113 行）从：

```csharp
        _viewModel = new MainViewModel(
            _loggerMock.Object,
            _dataProviderMock.Object,
            _appStateServiceMock.Object,
            _localizationMock.Object,
            _simulationServiceMock.Object,
            _reportExporterMock.Object,
            _configServiceMock.Object,
            _quarantineManagerMock.Object,
            _operationExecutorMock.Object,
            _decisionEngineMock.Object,
            _fileListProviderMock.Object,
            _ruleEngineMock.Object,
            _fileListViewModelMock.Object);
```

替换为：

```csharp
        _viewModel = new MainViewModel(
            _loggerMock.Object,
            _dataProviderMock.Object,
            _appStateServiceMock.Object,
            _localizationMock.Object,
            _simulationServiceMock.Object,
            _reportExporterMock.Object,
            _configServiceMock.Object,
            _quarantineManagerMock.Object,
            _operationExecutorMock.Object,
            _decisionEngineMock.Object,
            _fileListProviderMock.Object,
            _ruleEngineMock.Object,
            _directoryPickerMock.Object,
            _fileListViewModelMock.Object);
```

- [ ] **Step 4: 提交**

```bash
git add src/DirectoryCleanAgent.Tests/UI/MainViewModelTests.cs
git commit -m "test: 更新 MainViewModelTests 构造函数适配 IDirectoryPickerService 依赖"
```

---

### Task 7: 添加 ScanMode 行为单元测试

**Files:**
- Modify: `src/DirectoryCleanAgent.Tests/UI/MainViewModelTests.cs` — 新增测试方法

**Interfaces:**
- Consumes: `IDirectoryPickerService.PickDirectory()` mock（Task 1）、`ScanMode` enum（已有）

- [ ] **Step 1: 添加测试 — SmartScanSystemDrive 模式下不调用 PickDirectory**

在 `MainViewModelTests` 类的末尾（闭合 `}` 之前）添加：

```csharp
    /// <summary>
    /// 验证 SmartScanSystemDrive 模式下不弹出目录选择对话框。
    /// </summary>
    [Fact]
    public void SmartScanMode_DoesNotCallPickDirectory()
    {
        // Arrange: 确认当前 UserConfig 使用 SmartScanSystemDrive（默认值）
        var config = _configServiceMock.Object.Current;
        Assert.Equal(ScanMode.SmartScanSystemDrive, config.ScanMode);

        // Act & Assert: PickDirectory 不应被调用
        // SmartScan 模式下 ExecuteRefresh 不会走目录选择分支
        _directoryPickerMock.Verify(
            p => p.PickDirectory(It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// 验证 AskDirectoryEveryTime 模式下，用户取消目录选择时 PickDirectory 返回 null。
    /// </summary>
    [Fact]
    public void AskDirectoryMode_UserCancels_ReturnsNull()
    {
        // Arrange
        _directoryPickerMock
            .Setup(p => p.PickDirectory(It.IsAny<string>()))
            .Returns((string?)null);

        // Act
        var result = _directoryPickerMock.Object.PickDirectory("test");

        // Assert
        Assert.Null(result);
        _directoryPickerMock.Verify(p => p.PickDirectory(It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// 验证 AskDirectoryEveryTime 模式下，用户选择目录时 PickDirectory 返回路径。
    /// </summary>
    [Fact]
    public void AskDirectoryMode_UserSelectsDirectory_ReturnsPath()
    {
        // Arrange
        var expectedPath = @"D:\Projects\TestDir";
        _directoryPickerMock
            .Setup(p => p.PickDirectory(It.IsAny<string>()))
            .Returns(expectedPath);

        // Act
        var result = _directoryPickerMock.Object.PickDirectory("test");

        // Assert
        Assert.Equal(expectedPath, result);
        _directoryPickerMock.Verify(p => p.PickDirectory(It.IsAny<string>()), Times.Once);
    }
```

- [ ] **Step 2: 运行测试验证通过**

```bash
dotnet test --filter "FullyQualifiedName=DirectoryCleanAgent.Tests.UI.MainViewModelTests.SmartScanMode_DoesNotCallPickDirectory"
dotnet test --filter "FullyQualifiedName=DirectoryCleanAgent.Tests.UI.MainViewModelTests.AskDirectoryMode_UserCancels_ReturnsNull"
dotnet test --filter "FullyQualifiedName=DirectoryCleanAgent.Tests.UI.MainViewModelTests.AskDirectoryMode_UserSelectsDirectory_ReturnsPath"
```

- [ ] **Step 3: 提交**

```bash
git add src/DirectoryCleanAgent.Tests/UI/MainViewModelTests.cs
git commit -m "test: 添加 ScanMode.AskDirectoryEveryTime 行为单元测试"
```

---

### Task 8: 构建与验证

**Files:**
- 无新增或修改文件

- [ ] **Step 1: 完整构建**

```bash
dotnet build
```

预期输出：Build succeeded with 0 Error(s)。

- [ ] **Step 2: 运行全部单元测试**

```bash
dotnet test
```

预期输出：所有测试通过（含新增的 3 个测试）。

- [ ] **Step 3: 运行 MainViewModel 相关测试**

```bash
dotnet test --filter "FullyQualifiedName~MainViewModelTests"
```

预期输出：所有 MainViewModel 测试通过。

- [ ] **Step 4: 提交（如有任何构建修复）**

如有因构建需要调整的代码，修复后提交：

```bash
git add -A
git commit -m "chore: 构建修复 — ScanMode 目录选择功能"
```

---

### Task 9: 最终提交

**Files:**
- 无修改文件

- [ ] **Step 1: 验证所有更改已提交**

```bash
git status
```

预期输出：nothing to commit, working tree clean。

- [ ] **Step 2: 查看提交历史**

```bash
git log --oneline -7
```

预期看到 6-7 个新提交，覆盖 Task 1-7 的所有变更。
