# 文件列表"打开文件所在目录"功能设计

**日期**: 2026-07-18  
**状态**: 已批准  
**范围**: 在 FileListView 右键菜单新增"打开文件所在目录"选项

## 需求

用户在文件列表中右键文件时，可以通过"打开文件所在目录"菜单项，调用 `explorer.exe /select` 在 Windows 资源管理器中定位到该文件。

## 设计方案

### 涉及文件

| 文件 | 改动类型 |
|------|----------|
| `src/DirectoryCleanAgent/ViewModels/FileListViewModel.cs` | 修改 — 新增命令 + 执行方法 |
| `src/DirectoryCleanAgent/Controls/FileListView.xaml` | 修改 — 右键菜单新增 MenuItem |

### ViewModel 改动 (`FileListViewModel.cs`)

**1. 命令声明**（约第 360 行，与其他 `RelayCommand<FileListItem>` 放在一起）:

```csharp
public RelayCommand<FileListItem> OpenFileLocationCommand { get; }
```

**2. 构造函数中初始化**（约第 397 行，与其他命令初始化放在一起）:

```csharp
OpenFileLocationCommand = new RelayCommand<FileListItem>(ExecuteOpenFileLocation);
```

**3. 执行方法**（放在其他 Execute 方法旁边，约第 1478 行 `ExecuteViewDetail` 之后）:

```csharp
private void ExecuteOpenFileLocation(FileListItem? item)
{
    if (item == null) return;

    string path = item.FullPath;
    // 去除 \\?\ 前缀，explorer.exe 需要常规路径
    if (path.StartsWith(@"\\?\"))
        path = path[4..];

    if (!File.Exists(path) && !Directory.Exists(path))
    {
        MessageBox.Show($"文件不存在: {path}", "无法定位",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    try
    {
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
    catch (Exception ex)
    {
        MessageBox.Show($"无法打开文件目录: {ex.Message}", "错误",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

### XAML 改动 (`FileListView.xaml`)

在 `</ContextMenu>` 结束标签前（约第 196 行）插入:

```xml
<Separator />
<MenuItem Header="📂 打开文件所在目录"
          Command="{Binding OpenFileLocationCommand}"
          CommandParameter="{Binding PlacementTarget.SelectedItem,
              RelativeSource={RelativeSource AncestorType=ContextMenu}}" />
```

**完整右键菜单效果**:
- 🤖 AI 分析此文件
- ────────
- 🤖 AI 分析所有勾选文件
- ────────
- 📂 打开文件所在目录  ← 新增

### 设计决策

1. **命令类型**: `RelayCommand<FileListItem>` — 与现有 `RequestAiAnalysisCommand` / `ViewDetailCommand` 模式一致
2. **路径处理**: `explorer.exe /select` 需要常规路径（非 `\\?\` 格式），执行前去除前缀
3. **文件不存在**: 先检查文件是否存在，不存在时弹 Window 提示，不抛异常
4. **异常处理**: try-catch 包裹 Process.Start，防止 explorer.exe 不可用等极端情况
5. **完全限定名**: 使用 `System.Diagnostics.Process.Start(...)` 而不新增 using（`FileListViewModel.cs` 无 `System.Diagnostics` using，WPF 隐式 using 也不包含它）

## 不做什么

- 不支持对分组节点（FileGroupNode）打开目录
- 不添加键盘快捷键
- 不修改 FileListItem 模型
