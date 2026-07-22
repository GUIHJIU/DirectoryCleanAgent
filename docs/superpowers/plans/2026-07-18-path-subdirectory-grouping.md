# 路径分组 — 二级子目录钻取 实现方案

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 当一级分组为"路径"时，二级分组不再走交叉维度（时间/类型/操作建议/语义标签），改为展示更深一级子目录，形成两级目录钻取。

**Architecture:** 在 `FileListViewModel` 中新增 `BuildPathSubGroups` 方法和 `GetSecondLevelDirectory` 辅助方法，修改 `ApplySubGrouping` 入口让路径模式走独立分支。`MainWindow.xaml` 中新增二级分组 ComboBox，路径模式下禁用并显示"子目录"。现有的 `FileGroupNode` 模型、`HierarchicalDataTemplate` 模板、`CollectFileKeys` 递归收集全部复用，不做改动。

**Tech Stack:** C# + .NET 8 + WPF

## Global Constraints

- 树层级固定 2 层（Depth 0/1），不做无限递归
- 二级节点的 `TotalSizeBytes` = 该子目录下所有嵌套文件的递归求和（不仅是直属文件）
- `FileCacheKeys` 同样递归包含所有嵌套文件
- 路径处理保持与现有 `GetTopLevelDirectory` 一致的 `\\?\` 前缀剥离逻辑
- 文件直接在顶层目录下时归入 `"(根目录文件)"` 叶子节点
- 路径模式下二级 ComboBox 禁用，显示文字"子目录"

---

### Task 1: 新增 `GetSecondLevelDirectory` 辅助方法

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs`

**Interfaces:**
- Produces: `private static string? GetSecondLevelDirectory(string filePath)` — 返回根目录下第二级目录名，无则为 null

- [ ] **Step 1: 在 `GetTopLevelDirectory` 方法后添加 `GetSecondLevelDirectory`**

在 `FileListViewModel.cs` 的 `GetTopLevelDirectory` 方法（约第 731 行）之后，紧邻添加：

```csharp
/// <summary>从 \\?\ 格式路径中提取第二级目录名（路径钻取用）</summary>
private static string? GetSecondLevelDirectory(string filePath)
{
    try
    {
        string cleanPath = filePath.StartsWith(@"\\?\") ? filePath[4..] : filePath;
        string? root = Path.GetPathRoot(cleanPath);
        if (root == null) return null;

        string relativePath = cleanPath[root.Length..].TrimStart(Path.DirectorySeparatorChar);
        int firstSep = relativePath.IndexOf(Path.DirectorySeparatorChar);
        if (firstSep < 0) return null; // 文件直接在根目录下，无第一级

        // 跳过第一级目录，取第二级
        string beyondFirst = relativePath[(firstSep + 1)..];
        int secondSep = beyondFirst.IndexOf(Path.DirectorySeparatorChar);
        return secondSep > 0 ? beyondFirst[..secondSep] : null; // null = 文件在一级目录的直属
    }
    catch
    {
        return null;
    }
}
```

- [ ] **Step 2: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent/DirectoryCleanAgent.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs
git commit -m "feat: add GetSecondLevelDirectory helper for path subdirectory drilling"
```

---

### Task 2: 新增 `BuildPathSubGroups` 方法

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs`

**Interfaces:**
- Consumes: `GetSecondLevelDirectory(string filePath)` (from Task 1)
- Produces: `private ObservableCollection<FileGroupNode> BuildPathSubGroups(IReadOnlyList<FileDecisionCache> files, FileGroupNode parent)` — 将属于同一顶层目录的文件按第二级目录分桶

- [ ] **Step 1: 在 `ApplySubGrouping` 方法前添加 `BuildPathSubGroups`**

在 `FileListViewModel.cs` 的 `ApplySubGrouping` 方法（约第 656 行）之前添加：

```csharp
/// <summary>
/// 路径模式专用二级分组：按第二级子目录分桶。
/// 文件直接在顶层目录下时归入"(根目录文件)"节点。
/// 每个二级节点的 ItemCount/TotalSizeBytes 为该子目录下所有嵌套文件的递归汇总。
/// </summary>
private static ObservableCollection<FileGroupNode> BuildPathSubGroups(
    IReadOnlyList<FileDecisionCache> files,
    FileGroupNode parent)
{
    if (files.Count == 0) return new ObservableCollection<FileGroupNode>();

    var subDirGroups = files
        .GroupBy(f => GetSecondLevelDirectory(f.FilePath))
        .Select(g => new FileGroupNode
        {
            Label = g.Key ?? "(根目录文件)",
            Icon = g.Key != null ? "📁" : "📄",
            ItemCount = g.Count(),
            TotalSizeBytes = g.Sum(f => f.SizeBytes),
            Depth = parent.Depth + 1,
            Parent = parent,
            FileCacheKeys = g.Select(f => f.FilePath).ToList()
        })
        .OrderByDescending(g => g.TotalSizeBytes)
        .ToList();

    return new ObservableCollection<FileGroupNode>(subDirGroups);
}
```

- [ ] **Step 2: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent/DirectoryCleanAgent.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs
git commit -m "feat: add BuildPathSubGroups for path-mode secondary directory drilling"
```

---

### Task 3: 修改 `ApplySubGrouping` — 路径模式下走独立分支

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs`

**Interfaces:**
- Consumes: `BuildPathSubGroups(IReadOnlyList<FileDecisionCache>, FileGroupNode)` (from Task 2)
- Modifies: `ApplySubGrouping` 的行为 — 当 primaryMode == 0 时走 `BuildPathSubGroups`，忽略 `subMode` 参数

- [ ] **Step 1: 修改 `ApplySubGrouping` 方法签名和逻辑**

当前代码（`FileListViewModel.cs:657-682`）：

```csharp
private ObservableCollection<FileGroupNode> ApplySubGrouping(
    IReadOnlyList<FileDecisionCache> files,
    int subMode,
    FileGroupNode parent)
{
    if (files.Count == 0) return new ObservableCollection<FileGroupNode>();

    var subGroups = subMode switch
    {
        1 => BuildGroupByTime(files),
        2 => BuildGroupByType(files),
        3 => BuildGroupByFinalAction(files),
        4 => BuildGroupBySemanticLabel(files),
        _ => new List<FileGroupNode>()
    };

    foreach (var child in subGroups)
    {
        child.Depth = parent.Depth + 1;
        child.Parent = parent;
    }

    return new ObservableCollection<FileGroupNode>(
        subGroups.OrderByDescending(g => g.TotalSizeBytes));
}
```

替换为：

```csharp
private ObservableCollection<FileGroupNode> ApplySubGrouping(
    IReadOnlyList<FileDecisionCache> files,
    int subMode,
    FileGroupNode parent,
    int primaryMode)
{
    if (files.Count == 0) return new ObservableCollection<FileGroupNode>();

    // 路径模式：二级固定为子目录钻取，忽略 subMode
    if (primaryMode == 0)
    {
        return BuildPathSubGroups(files, parent);
    }

    var subGroups = subMode switch
    {
        1 => BuildGroupByTime(files),
        2 => BuildGroupByType(files),
        3 => BuildGroupByFinalAction(files),
        4 => BuildGroupBySemanticLabel(files),
        _ => new List<FileGroupNode>()
    };

    foreach (var child in subGroups)
    {
        child.Depth = parent.Depth + 1;
        child.Parent = parent;
    }

    return new ObservableCollection<FileGroupNode>(
        subGroups.OrderByDescending(g => g.TotalSizeBytes));
}
```

- [ ] **Step 2: 更新 `BuildGroupTree` 中的调用点**

在 `BuildGroupTree` 方法（`FileListViewModel.cs:528-537`）中，修改 `ApplySubGrouping` 的调用，传入 `primaryMode`：

当前代码：

```csharp
// Step 3: 应用二级分组
if (secondaryMode > 0)
{
    foreach (var group in primaryGroups)
    {
        group.Children = ApplySubGrouping(actionable
            .Where(f => BelongsToGroup(f, group, primaryMode))
            .ToList(), secondaryMode, group);
    }
}
```

替换为：

```csharp
// Step 3: 应用二级分组
// 路径模式(primaryMode==0)始终生成子目录二级分组，忽略 secondaryMode
// 其他模式仅在 secondaryMode > 0 时生成二级分组
if (primaryMode == 0 || secondaryMode > 0)
{
    foreach (var group in primaryGroups)
    {
        group.Children = ApplySubGrouping(actionable
            .Where(f => BelongsToGroup(f, group, primaryMode))
            .ToList(), secondaryMode, group, primaryMode);
    }
}
```

- [ ] **Step 3: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent/DirectoryCleanAgent.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs
git commit -m "feat: route path-mode secondary grouping to subdirectory drilling"
```

---

### Task 4: 新增测试数据 — 多级路径深度

**Files:**
- Modify: `src/DirectoryCleanAgent.Tests/UI/TestDataFactory.cs`

**Interfaces:**
- Produces: `public static readonly List<FileDecisionCache> PathSubDirCache` — 包含多级子目录的路径测试数据

- [ ] **Step 1: 在 `TestDataFactory.cs` 中添加 `PathSubDirCache`**

在 `PathGroupedCache` 定义（约第 40 行）之后添加：

```csharp
/// <summary>
/// 路径二级子目录钻取测试数据。
/// 2个顶层目录（Windows/Users），
/// Windows 下有 3 个二级子目录（Temp/System32/Logs），
/// Users 下有 2 个二级子目录（Admin/Public），
/// Windows 根下有 1 个直属文件。
/// 总计 8 条文件。
/// </summary>
public static readonly List<FileDecisionCache> PathSubDirCache = new()
{
    // Windows\Temp\ (2 files)
    CreateCache(@"\\?\C:\Windows\Temp\file1.tmp", "SystemTempFiles", FinalAction.AutoDelete, 10_000_000, -1),
    CreateCache(@"\\?\C:\Windows\Temp\sub\file2.tmp", "SystemTempFiles", FinalAction.AutoDelete, 5_000_000, -1),
    // Windows\System32\ (1 file)
    CreateCache(@"\\?\C:\Windows\System32\file3.dll", "Protected", FinalAction.SuggestDelete, 20_000_000, -2),
    // Windows\Logs\ (1 file)
    CreateCache(@"\\?\C:\Windows\Logs\file4.log", "LogsAndDumps", FinalAction.ManualReview, 30_000_000, -3),
    // Windows 根目录直属 (1 file — 无二级子目录)
    CreateCache(@"\\?\C:\Windows\root_file.dat", "Uncategorized", FinalAction.SuggestDelete, 8_000_000, -1),
    // Users\Admin\ (2 files)
    CreateCache(@"\\?\C:\Users\Admin\file5.txt", "Uncategorized", FinalAction.SuggestDelete, 40_000_000, -4),
    CreateCache(@"\\?\C:\Users\Admin\AppData\file6.dat", "AppCache", FinalAction.AutoDelete, 50_000_000, -5),
    // Users\Public\ (1 file)
    CreateCache(@"\\?\C:\Users\Public\file7.zip", "OldDownloads", FinalAction.SuggestDelete, 60_000_000, -6),
};
```

- [ ] **Step 2: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent.Tests/DirectoryCleanAgent.Tests.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DirectoryCleanAgent.Tests/UI/TestDataFactory.cs
git commit -m "test: add PathSubDirCache for path subdirectory drilling tests"
```

---

### Task 5: 新增单元测试 — 路径子目录钻取

**Files:**
- Modify: `src/DirectoryCleanAgent.Tests/UI/FileListViewModelTests.cs`

**Interfaces:**
- Consumes: `TestDataFactory.PathSubDirCache` (from Task 4), `BuildPathSubGroups` + `GetSecondLevelDirectory` (from Tasks 1-3)

- [ ] **Step 1: 添加"路径模式默认生成子目录二级分组"测试**

在 `SubGrouping_PathThenTime` 测试（约第 228 行）之后添加：

```csharp
[Fact]
public async Task PathMode_AlwaysGeneratesSubdirectoryChildren()
{
    // Arrange: 路径模式，secondaryMode=0（无），也应生成子目录分组
    _mockCacheRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(TestDataFactory.PathSubDirCache);

    _viewModel.GroupByPrimaryIndex = 0;   // 路径
    _viewModel.GroupBySecondaryIndex = 0; // 无（路径模式下被忽略，固定子目录）
    await _viewModel.LoadDataAsync();

    // Assert: 两个一级节点（Windows, Users），按大小降序
    Assert.Equal(2, _viewModel.GroupTree.Count);

    // Users 有 Admin + Public = 150MB，应排在 Windows 前面
    var firstGroup = _viewModel.GroupTree[0];
    Assert.Equal("Users", firstGroup.Label);
    Assert.NotEmpty(firstGroup.Children);
    Assert.Equal(2, firstGroup.Children.Count); // Admin + Public

    var secondGroup = _viewModel.GroupTree[1];
    Assert.Equal("Windows", secondGroup.Label);
    Assert.NotEmpty(secondGroup.Children);
    Assert.Equal(3, secondGroup.Children.Count); // Temp + Logs + System32（按大小降序：Logs 30M, System32 20M, Temp 15M）

    // 验证二级节点排序：按 TotalSizeBytes 降序
    var windowsChildren = secondGroup.Children;
    Assert.True(windowsChildren[0].TotalSizeBytes >= windowsChildren[1].TotalSizeBytes);
    Assert.True(windowsChildren[1].TotalSizeBytes >= windowsChildren[2].TotalSizeBytes);

    // 验证 Users 的 Admin 二级节点包含递归文件（admin 直属 + AppData 嵌套）
    var adminNode = firstGroup.Children.First(c => c.Label == "Admin");
    Assert.Equal(2, adminNode.ItemCount);          // file5.txt + file6.dat（递归）
    Assert.Equal(90_000_000, adminNode.TotalSizeBytes); // 40MB + 50MB
}
```

- [ ] **Step 2: 添加"根目录直属文件归入占位节点"测试**

```csharp
[Fact]
public async Task PathMode_FilesDirectlyUnderTopDir_GoToPlaceholderNode()
{
    // Arrange
    _mockCacheRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(TestDataFactory.PathSubDirCache);

    _viewModel.GroupByPrimaryIndex = 0;
    await _viewModel.LoadDataAsync();

    // Assert: Windows 有 3 个子节点，其中之一是"(根目录文件)"
    var windowsGroup = _viewModel.GroupTree.First(g => g.Label == "Windows");
    var placeholder = windowsGroup.Children.FirstOrDefault(c => c.Label == "(根目录文件)");
    Assert.NotNull(placeholder);
    Assert.Equal("📄", placeholder.Icon);   // 非目录图标
    Assert.Equal(1, placeholder.ItemCount);  // root_file.dat
    Assert.Equal(8_000_000, placeholder.TotalSizeBytes);
}
```

- [ ] **Step 3: 添加"点击二级节点过滤右侧文件列表"测试**

```csharp
[Fact]
public async Task PathMode_SelectSecondaryNode_FiltersDataGrid()
{
    // Arrange
    _mockCacheRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(TestDataFactory.PathSubDirCache);

    _viewModel.GroupByPrimaryIndex = 0;
    await _viewModel.LoadDataAsync();

    // Act: 点击 Windows → Temp 二级节点
    var windowsGroup = _viewModel.GroupTree.First(g => g.Label == "Windows");
    var tempNode = windowsGroup.Children.First(c => c.Label == "Temp");
    tempNode.IsSelected = true;

    await WaitForViewModelAsync();

    // Assert: 右侧列表应显示 Temp 子目录树下所有文件（含嵌套 sub\file2.tmp）
    Assert.Equal(tempNode.ItemCount, _viewModel.CurrentFileList.Count);
    Assert.Equal(2, _viewModel.CurrentFileList.Count);
    Assert.All(_viewModel.CurrentFileList,
        item => Assert.Contains(@"Windows\Temp\", item.FullPath));
}
```

- [ ] **Step 4: 运行新增测试，确认全部通过**

```bash
dotnet test --filter "FullyQualifiedName~PathMode_"
```

Expected: 3 passed, 0 failed.

- [ ] **Step 5: 运行全部测试确保无回归**

```bash
dotnet test
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/DirectoryCleanAgent.Tests/UI/FileListViewModelTests.cs
git commit -m "test: add path subdirectory drilling unit tests"
```

---

### Task 6: 新建可计算属性 `SubGroupHeaderText` — 路径模式下显示"子目录"

**Files:**
- Modify: `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs`

**Interfaces:**
- Produces: `public string SubGroupHeaderText` — 路径模式返回"子目录"，其他模式返回 `SubGroupModes[GroupBySecondaryIndex]`

- [ ] **Step 1: 在 VM 中添加 `SubGroupHeaderText` 属性**

在 `SubGroupModes` 静态属性（`FileListViewModel.cs:118-121`）之后添加：

```csharp
/// <summary>
/// 二级分组下拉框显示文本。
/// 路径模式(primaryMode==0)固定为"子目录"并禁用下拉选择；
/// 其他模式显示选中的 SubGroupModes 项。
/// </summary>
public string SubGroupHeaderText =>
    _groupByPrimaryIndex == 0 ? "子目录" : SubGroupModes[_groupBySecondaryIndex];

/// <summary>二级分组下拉框是否可交互（路径模式下禁用）</summary>
public bool IsSubGroupEnabled => _groupByPrimaryIndex != 0;
```

- [ ] **Step 2: 在 `GroupByPrimaryIndex` setter 中触发变更通知**

修改 `GroupByPrimaryIndex` setter（`FileListViewModel.cs:84-91`）：

当前代码：

```csharp
if (SetProperty(ref _groupByPrimaryIndex, value))
{
    _currentLoadCts?.Cancel();
    _currentLoadCts?.Dispose();
    _currentLoadCts = new CancellationTokenSource();
    _ = RebuildGroupTreeAsync(_currentLoadCts.Token);
}
```

替换为：

```csharp
if (SetProperty(ref _groupByPrimaryIndex, value))
{
    OnPropertyChanged(nameof(SubGroupHeaderText));
    OnPropertyChanged(nameof(IsSubGroupEnabled));
    _currentLoadCts?.Cancel();
    _currentLoadCts?.Dispose();
    _currentLoadCts = new CancellationTokenSource();
    _ = RebuildGroupTreeAsync(_currentLoadCts.Token);
}
```

- [ ] **Step 3: 在 `GroupBySecondaryIndex` setter 中触发变更通知**

修改 `GroupBySecondaryIndex` setter（`FileListViewModel.cs:101-108`），在 `SetProperty` 成功后添加：

```csharp
if (SetProperty(ref _groupBySecondaryIndex, value))
{
    OnPropertyChanged(nameof(SubGroupHeaderText));
    _currentLoadCts?.Cancel();
    _currentLoadCts?.Dispose();
    _currentLoadCts = new CancellationTokenSource();
    _ = RebuildGroupTreeAsync(_currentLoadCts.Token);
}
```

- [ ] **Step 4: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent/DirectoryCleanAgent.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs
git commit -m "feat: add SubGroupHeaderText and IsSubGroupEnabled for subdirectory mode indicator"
```

---

### Task 7: MainWindow.xaml — 添加二级分组 ComboBox

**Files:**
- Modify: `src/DirectoryCleanAgent/MainWindow.xaml`

**Interfaces:**
- Consumes: `SubGroupHeaderText`, `IsSubGroupEnabled`, `SubGroupModes`, `GroupBySecondaryIndex` (from Tasks 1-6)

- [ ] **Step 1: 在一级分组 ComboBox 旁添加二级分组 ComboBox**

在 `MainWindow.xaml` 第 180 行（一级 ComboBox 的 `</StackPanel>` 关闭后）添加二级分组 StackPanel：

当前代码（`MainWindow.xaml:171-180`）：

```xaml
<!-- 分组依据下拉框（C2：绑定到 FileListViewModel） -->
<StackPanel Grid.Column="7" Orientation="Horizontal" VerticalAlignment="Center">
    <TextBlock Text="分组依据:" FontSize="13"
               Foreground="{StaticResource CardTextSecondaryBrush}"
               VerticalAlignment="Center" Margin="0,0,6,0" />
    <ComboBox Style="{StaticResource GroupByComboBoxStyle}"
              ItemsSource="{Binding FileListViewModel.GroupByModes}"
              SelectedIndex="{Binding FileListViewModel.GroupByPrimaryIndex}"
              IsEnabled="{Binding FileListViewModel.IsGroupedView}" />
</StackPanel>
```

替换为：

```xaml
<!-- 分组依据下拉框（C2：绑定到 FileListViewModel） -->
<StackPanel Grid.Column="7" Orientation="Horizontal" VerticalAlignment="Center">
    <TextBlock Text="分组依据:" FontSize="13"
               Foreground="{StaticResource CardTextSecondaryBrush}"
               VerticalAlignment="Center" Margin="0,0,6,0" />
    <ComboBox Style="{StaticResource GroupByComboBoxStyle}"
              ItemsSource="{Binding FileListViewModel.GroupByModes}"
              SelectedIndex="{Binding FileListViewModel.GroupByPrimaryIndex}"
              IsEnabled="{Binding FileListViewModel.IsGroupedView}" />
    <!-- 二级分组下拉框 -->
    <TextBlock Text=" / 二级:"
               FontSize="13"
               Foreground="{StaticResource CardTextSecondaryBrush}"
               VerticalAlignment="Center" Margin="6,0,4,0" />
    <TextBlock Text="{Binding FileListViewModel.SubGroupHeaderText}"
               FontSize="13"
               Foreground="{StaticResource CardTextSecondaryBrush}"
               VerticalAlignment="Center"
               Visibility="{Binding FileListViewModel.IsSubGroupEnabled, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}" />
    <ComboBox Style="{StaticResource GroupByComboBoxStyle}"
              ItemsSource="{Binding FileListViewModel.SubGroupModes}"
              SelectedIndex="{Binding FileListViewModel.GroupBySecondaryIndex}"
              IsEnabled="{Binding FileListViewModel.IsSubGroupEnabled}"
              Visibility="{Binding FileListViewModel.IsSubGroupEnabled, Converter={StaticResource BoolToVisibilityConverter}}" />
</StackPanel>
```

**说明：** 当路径模式时，`IsSubGroupEnabled` 为 `false`，ComboBox 隐藏，`SubGroupHeaderText`（"子目录"）的 TextBlock 显示；其他模式时 ComboBox 可见可交互，TextBlock 隐藏。

- [ ] **Step 2: 检查 `BoolToVisibilityConverter` 是否支持 `ConverterParameter=Invert`**

如果当前 `BoolToVisibilityConverter` 不支持 invert 参数，有两个选择：
1. 给 `BoolToVisibilityConverter` 添加 `ConverterParameter="Invert"` 支持
2. 在 VM 中新增 `public bool IsSubGroupEnabledInverse => _groupByPrimaryIndex == 0;` 属性

**检查方式：**

```bash
# 确认当前 BoolToVisibilityConverter 实现
```

查找文件：

```powershell
Get-ChildItem -Recurse -Path "src/DirectoryCleanAgent" -Filter "*BoolToVisibility*"
```

如果当前实现不支持 invert，采用方案 2 — 在 Task 6 中追加一个 `IsSubGroupEnabledInverse` 属性。

**简化方案（推荐）：直接在 Task 6 中追加属性，避免改 Converter**

在 Task 6 的 Step 1 中追加：

```csharp
/// <summary>二级分组下拉框隐藏状态（路径模式为 true）</summary>
public bool IsSubGroupReadOnly => _groupByPrimaryIndex == 0;
```

并在 `GroupByPrimaryIndex` setter 中追加 `OnPropertyChanged(nameof(IsSubGroupReadOnly))`。

XAML 改为：

```xaml
<TextBlock Text="{Binding FileListViewModel.SubGroupHeaderText}"
           FontSize="13"
           Foreground="{StaticResource CardTextSecondaryBrush}"
           VerticalAlignment="Center"
           Visibility="{Binding FileListViewModel.IsSubGroupReadOnly, Converter={StaticResource BoolToVisibilityConverter}}" />
<ComboBox Style="{StaticResource GroupByComboBoxStyle}"
          ItemsSource="{Binding FileListViewModel.SubGroupModes}"
          SelectedIndex="{Binding FileListViewModel.GroupBySecondaryIndex}"
          IsEnabled="{Binding FileListViewModel.IsSubGroupEnabled}"
          Visibility="{Binding FileListViewModel.IsSubGroupEnabled, Converter={StaticResource BoolToVisibilityConverter}}" />
```

- [ ] **Step 3: 验证编译通过**

```bash
dotnet build src/DirectoryCleanAgent/DirectoryCleanAgent.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/DirectoryCleanAgent/MainWindow.xaml
git commit -m "feat: add secondary grouping ComboBox with path-mode subdirectory override"
```

---

### Task 8: 回归测试 — 确保现有分组测试通过

**Files:**
- （无修改，仅运行测试）

- [ ] **Step 1: 运行完整测试套件**

```bash
dotnet test
```

Expected: All tests pass, with special attention to:
- `SubGrouping_PathThenTime` — 此测试原来期望路径+时间交叉分组有 Children。现在路径模式下 Children 走子目录逻辑而非时间分组。需要确认此测试是否仍然合理。

**如果 `SubGrouping_PathThenTime` 失败：** 更新该测试以反映新行为：
- 路径模式下 `GroupBySecondaryIndex` 的值被忽略，Children 始终为子目录分组
- 修改断言：不再验证 Children 是按时间分组，而是验证 Children 是子目录分组

修改方案（`FileListViewModelTests.cs:228-242`）：

```csharp
[Fact]
public async Task SubGrouping_PathThenTime()
{
    // PathGroupedCache: C:\Windows\Temp\, C:\Windows\System32\, C:\Windows\Logs\
    //                    C:\Users\Admin\ (3 files)
    // GetTopLevelDirectory: "Windows" (3 files), "Users" (3 files)
    // GetSecondLevelDirectory for Windows files: "Temp", "System32", "Logs"
    // GetSecondLevelDirectory for Users files: "Admin" (all 3 are in Admin/)
    _mockCacheRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(TestDataFactory.PathGroupedCache);

    _viewModel.GroupByPrimaryIndex = 0;   // 路径
    _viewModel.GroupBySecondaryIndex = 1; // 被忽略，路径模式固定走子目录
    await _viewModel.LoadDataAsync();

    // 路径模式下二级分组始终为子目录钻取
    foreach (var group in _viewModel.GroupTree)
    {
        Assert.NotEmpty(group.Children);
        // 每个子节点的 Label 是二级目录名，不是时间桶标签
        foreach (var child in group.Children)
        {
            Assert.DoesNotContain("天", child.Label); // 不应出现时间标签
        }
    }
}
```

- [ ] **Step 2: 如有修改，提交**

```bash
git add src/DirectoryCleanAgent.Tests/UI/FileListViewModelTests.cs
git commit -m "test: update SubGrouping_PathThenTime to match new subdirectory behavior"
```

---

## 自检清单

1. **Spec 覆盖：**
   - ✅ 路径模式二级固定为子目录钻取 → Task 3
   - ✅ 二级子目录节点大小递归汇总 → Task 2
   - ✅ 根目录直属文件归入占位节点 → Task 2
   - ✅ 路径模式二级 ComboBox 禁用显示"子目录" → Task 6, 7
   - ✅ 现有交叉分组模式不受影响 → Task 3 中 `primaryMode != 0` 走原逻辑

2. **占位符扫描：** ✅ 无 TBD/TODO/模糊描述

3. **类型一致性：**
   - `BuildPathSubGroups` 返回 `ObservableCollection<FileGroupNode>` → `ApplySubGrouping` 也返回同一类型 ✅
   - `GetSecondLevelDirectory` 返回 `string?` → `BuildPathSubGroups` 中以 `g.Key ?? "(根目录文件)"` 处理 null ✅
   - `SubGroupHeaderText` 返回 `string` → XAML 绑定到 `TextBlock.Text` ✅
   - `IsSubGroupEnabled` / `IsSubGroupReadOnly` 返回 `bool` → XAML 绑定到 `IsEnabled` / `Visibility` ✅
