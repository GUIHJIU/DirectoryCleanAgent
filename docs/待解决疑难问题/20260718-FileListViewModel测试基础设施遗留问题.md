# FileListViewModel 测试基础设施遗留问题

- **发现日期**: 2026-07-18
- **状态**: 待处理
- **优先级**: 低~中(可测试性债务 + 潜在 flaky,当前未直接阻塞)

修复"显示所有文件模式下刷新扫描覆盖列表"bug(见
`docs/superpowers` 同期改动与 `FileListViewModelTests.LoadDataAsync_InShowAllMode_KeepsEverythingFileListNotGroupData`)
过程中,连带发现以下未处理的遗留问题。

## 问题 A:GroupByChanged 测试存在同款等待条件竞态(潜在 flaky)

`FileListViewModelTests.GroupByChanged_NewTreeNodes_ClickStillFiltersFileList`
(FileListViewModelTests.cs:607,"Act 2/Assert"段)使用等待条件:

```csharp
await WaitForConditionAsync(() =>
    ReferenceEquals(_viewModel.SelectedGroup, target) &&
    _viewModel.CurrentFileList.Count == target.ItemCount);
```

与 2026-07-18 已修复的 `GroupNode_IsSelectedTrue_UpdatesSelectedGroupAndFiltersFileList`
是**同一竞态模式**:`SelectedGroup` 在 setter 中同步更新,而 `CurrentFileList`
由 fire-and-forget 的 `OnGroupSelectedAsync` 异步填充。当切换前后两个分组的
`ItemCount` 恰好相等时,等待条件在列表尚未切换时即通过,随后的内容断言竞态失败。

**修法参照已修复测试**:等待条件中追加
`CurrentFileList.All(item => target.FileCacheKeys.Contains(item.FullPath))`。
本次未顺手修改的原因:该测试当日未失败,按最小改动原则留待专项处理。

## 问题 B:WaitForViewModelAsync 是"无条件等满超时",不是条件等待

`FileListViewModelTests.WaitForViewModelAsync`:

```csharp
private static async Task WaitForViewModelAsync(int timeoutMs = 2000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(25);
    }
}
```

循环无退出条件,**必然等满 2 秒**——既拖慢每个使用它的测试 2 秒,
又不保证异步链路真正完成(负载高时 2 秒可能不够)。
当前使用方:`PathMode_SelectSecondaryNode_FiltersDataGrid`。

**建议**:删除该方法,调用点改用 `WaitForConditionAsync` 传入明确条件。

## 问题 C:ViewModel 中仍有 4 处裸 Dispatcher 调用,对应路径不可单测

`FileListViewModel` 提供了测试兼容的 `RunOnUIThreadAsync`
(无 `Application.Current` 时走锁内同步执行),但以下位置仍直接调用
`Application.Current.Dispatcher.InvokeAsync`,在无 WPF Application 的
单元测试环境抛 NRE 并被各自的 catch 吞掉,导致对应功能路径**无法单测**:

| 位置(2026-07-18 行号) | 所在方法/路径 |
|---|---|
| FileListViewModel.cs:1014 | 排序状态更新路径 |
| FileListViewModel.cs:1105 | `ApplySortAsync` 全量模式分支 |
| FileListViewModel.cs:1139 | `ApplySortAsync` 分组模式分支 |
| FileListViewModel.cs:1434 | 增量刷新/事件处理路径 |

同款问题已在 `LoadAllFilesFromEverythingAsync` 修复(改用 `RunOnUIThreadAsync`,
2026-07-18)。上述 4 处属机械替换,但涉及排序/增量刷新行为,建议替换后
为 `ApplySortAsync` 补排序下推的单元测试(现有
`SortColumnMap_ContainsAllExpectedColumns` 等两个排序测试是 `Assert.True(true)`
的占位空测试,无实际覆盖)。

## 问题 D:IEverythingSdk 接口注释与实际实现不符

`Interop/IEverythingSdk.cs` 头注释声称:

> 适配 SDK 3.0 / Everything ≥ 1.5 ……生产环境使用 Everything3SdkWrapper

实际情况(2026-07-18 核实):

- 仓库中**不存在** `Everything3SdkWrapper` 类;唯一实现是
  `EverythingSdkWrapper`,底层调用 `EverythingNative`(SDK 2.0 IPC API);
- `Everything3Native.cs` 的 P/Invoke 声明仅被 `EverythingDependencyDetector`
  用于依赖检测,与查询主链路无关。

注释误导阅读者判断查询走的 SDK 版本(排查 Bug 时曾造成困惑)。
**建议**:修正接口注释为实际架构,或注明"SDK 3.0 迁移未完成,当前主链路为 SDK 2.0"。

## 关联记录

- [20260718-BackupManager回滚进度测试偶发失败.md](20260718-BackupManager回滚进度测试偶发失败.md)(疑似同类异步断言竞态)
- [20260718-集成测试环境敏感与测试宿主中断.md](20260718-集成测试环境敏感与测试宿主中断.md)
