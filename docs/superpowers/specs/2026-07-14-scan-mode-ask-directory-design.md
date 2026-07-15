# ScanMode "每次询问目录" 功能实现设计

**日期**: 2026-07-14
**状态**: 待审核
**范围**: 将设置页中已有的 `ScanMode.AskDirectoryEveryTime` 选项接入运行时扫描流程

---

## 1. 背景与目标

### 1.1 当前状态

`ScanMode` 枚举和 `UserConfig.ScanMode` 配置项已在数据模型层和设置 UI 层完成，但从未被运行时扫描流程消费。无论用户选择哪种扫描模式，`MainViewModel.ExecuteRefresh()` 始终使用 `config.IncludedVolumes`（默认 `"C:"`）决定扫描范围。

### 1.2 目标

- 当用户在设置中选择 `AskDirectoryEveryTime` 后，每次点击"刷新扫描"按钮时弹出目录选择对话框
- 用户选择目录后，仅扫描该目录（含递归子目录），使用 Everything `PathFilter` 精确限定
- 用户取消选择时，不启动扫描，保持当前界面状态不变
- 不改变启动流程（`StartupBehavior` 独立运作，启动时不做目录选择）

### 1.3 非目标

- 不修改 `StartupBehavior` 的接入逻辑
- 不修改 `SmartScanSystemDrive` 模式的行为
- 不引入新的 NuGet 包或第三方依赖

---

## 2. 架构决策

### 2.1 抽取 `IDirectoryPickerService`

将目录对话框调用抽象为可注入的服务接口，遵循项目 DI 架构风格。

- **接口**: `IDirectoryPickerService` — 放于 `src/DirectoryCleanAgent/Services/`，与 `IAppStateService` 同级
- **实现**: `DirectoryPickerService` — 放于同目录，调用 `System.Windows.Forms.FolderBrowserDialog`
- **生命周期**: Transient（无状态，每次解析创建新实例）

### 2.2 不使用 WPF 原生对话框

`System.Windows.Forms.FolderBrowserDialog` 已通过项目的 `global using System.Windows.Forms` 全局可用，且 `SaveFileDialog` 已在 `MainViewModel` 中直接使用。引入 WPF 替代方案需额外 NuGet 包或复杂 P/Invoke，收益有限。

### 2.3 路径标准化

用户选择的路径来自 `FolderBrowserDialog.SelectedPath`（普通格式，如 `D:\Projects`），需通过项目已有的 `PathNormalizer.Normalize()` 转换为 `\\?\D:\Projects` 格式后再赋值给 `EverythingQueryParams.PathFilter`。

---

## 3. 文件变更清单

### 3.1 新增文件

| 文件 | 说明 |
|------|------|
| `src/DirectoryCleanAgent/Services/IDirectoryPickerService.cs` | 目录选择器接口 |
| `src/DirectoryCleanAgent/Services/DirectoryPickerService.cs` | 目录选择器实现 |

### 3.2 修改文件

| 文件 | 修改内容 |
|------|----------|
| `src/DirectoryCleanAgent/App.xaml.cs` | `ConfigureServices()` 中注册 `IDirectoryPickerService` |
| `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs` | 构造注入 + `ExecuteRefresh()` 增加 ScanMode 判断 |
| `src/DirectoryCleanAgent.Core/Localization/Strings.resx` | 新增 `Main.Scan.SelectDirectory`（简体中文） |
| `src/DirectoryCleanAgent.Core/Localization/Strings.en.resx` | 新增 `Main.Scan.SelectDirectory`（English） |

---

## 4. 接口与实现设计

### 4.1 `IDirectoryPickerService`

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

### 4.2 `DirectoryPickerService`

- 使用 `System.Windows.Forms.FolderBrowserDialog`
- `Description` = 传入的本地化文本
- `UseDescriptionForTitle = true`（对话框标题栏显示描述文本）
- `ShowDialog() == DialogResult.OK` → 返回 `SelectedPath`，否则返回 `null`

---

## 5. 核心逻辑流程

```
用户点击"刷新扫描"（RefreshCommand）
        │
        ▼
  读取 config.ScanMode
        │
   ┌────┴──────────┐
   │               │
SmartScan    AskDirectoryEveryTime
   │               │
   ▼               ▼
直接构建    _directoryPicker.PickDirectory(desc)
queryParams        │
   │        ┌──────┴──────┐
   │        │             │
   │     返回路径      返回 null
   │        │             │
   │        ▼             ▼
   │  PathFilter     直接 return
   │  = Normalize(路径) 不启动扫描
   │        │         _logger.LogDebug(...)
   │        │
   └───┬────┘
       ▼
  queryParams.Volumes 使用 config.IncludedVolumes
  （当 PathFilter 非 null 时，PathFilter 精确限定目录范围）
       │
       ▼
  执行 B1→B2→B3 管道（后续逻辑不变）
```

### 5.1 关键实现细节

1. **PathFilter 优先于 Volumes**: 目录模式下 `PathFilter` 将范围精确限定到所选目录；`Volumes` 字段保持不变（来自 `IncludedVolumes`），作为辅助过滤
2. **路径标准化**: 调用 `PathNormalizer.Normalize()` 将用户选中的路径转为 `\\?\` 格式
3. **取消不改变状态**: 用户取消时直接 return，不修改 `AppState`、不弹出提示
4. **日志**: 用户选择路径时记录 `LogInformation`，取消时记录 `LogDebug`

---

## 6. MainViewModel 修改点

### 6.1 构造函数

新增 `IDirectoryPickerService` 参数，赋值给 `private readonly` 字段。

### 6.2 `ExecuteRefresh()` 方法

在创建 `_currentCts` 之前插入以下逻辑:

```csharp
if (config.ScanMode == ScanMode.AskDirectoryEveryTime)
{
    var pickedPath = _directoryPicker.PickDirectory(
        _localization.GetString("Main.Scan.SelectDirectory"));
    if (pickedPath == null)
    {
        _logger.LogDebug("用户取消了目录选择，扫描未启动");
        return;
    }
    queryParams = queryParams with
    {
        PathFilter = PathNormalizer.Normalize(pickedPath)
    };
    _logger.LogInformation("用户选择了扫描目录: {Path}", pickedPath);
}
```

### 6.3 新增 using

- `using DirectoryCleanAgent.Core.PathHandling;`
- `using DirectoryCleanAgent.Services;`（如尚无，用于 `IDirectoryPickerService`）

---

## 7. 本地化

### 7.1 新增资源键

| 键 | zh-CN | en-US |
|----|-------|-------|
| `Main.Scan.SelectDirectory` | 请选择要扫描的目录: | Select directory to scan: |

---

## 8. DI 注册

在 `App.xaml.cs` 的 `ConfigureServices()` 中，与其他 Transient 服务一起注册:

```csharp
services.AddTransient<IDirectoryPickerService, DirectoryPickerService>();
```

---

## 9. 测试策略

### 9.1 单元测试

- `DirectoryPickerService` 本身不编写单元测试（仅封装 Windows Forms 对话框，无业务逻辑，测试价值低）
- `MainViewModel` 已有 `SettingsViewModelTests` 模式的单元测试基础设施，可验证:
  - `ScanMode = AskDirectoryEveryTime` + 取消 → `ExecuteRefresh` 提前返回，`AppState` 不变
  - `ScanMode = SmartScanSystemDrive` → 不调用 `PickDirectory`，直接走原流程

### 9.2 集成测试

在 `MainViewModel` 集成测试中 mock `IDirectoryPickerService`:
- mock 返回路径 → 验证 `EverythingQueryParams.PathFilter` 被正确设置
- mock 返回 `null` → 验证扫描未启动，状态不变

---

## 10. 风险与边界

| 项目 | 说明 |
|------|------|
| 所选目录不存在 | `FolderBrowserDialog` 本身保证用户只能选择已存在的目录，无需额外校验 |
| 超长路径 | `PathNormalizer.Normalize()` 原生支持最长 32767 字符的扩展路径 |
| 非 NTFS 卷 | Everything 自身仅索引 NTFS 卷；用户选择非 NTFS 卷上的目录时，Everything 返回空结果，无需额外处理 |
| 无权限目录 | 用户选择无访问权限的目录时，Everything 在扫描阶段会跳过无权限文件，与现有行为一致 |
