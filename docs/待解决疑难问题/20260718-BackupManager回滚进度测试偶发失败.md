# BackupManager 回滚进度测试偶发失败

- **发现日期**: 2026-07-18
- **状态**: 待排查(根因未定位)
- **优先级**: 中(不影响功能,但污染 CI 信号)

## 现象

`DirectoryCleanAgent.Tests.Operations.BackupManagerTests.RollbackAsync_ShouldReportProgress`
在全量测试套件运行中**偶发失败**,单独运行时稳定通过。

观察数据(2026-07-18,修复文件列表 bug 期间的回归验证):

- 排除 Integration 的单元套件连跑 5 次:4 次全绿,1 次仅此测试失败
- 失败耗时仅 **32ms**(快速失败,非等待超时)
- 失败当次其余 788 个测试全部通过

## 已排除的原因

当日改动仅涉及 `FileListViewModel`、`EverythingFileListProvider` 及 Everything Interop
三件套,与 `DirectoryCleanAgent.Operations.BackupManager` 无任何共享代码路径,
可排除由该次改动引入。

## 初步分析(未验证)

"32ms 快速失败 + 仅在全套并行运行时出现"的模式符合**进度回调时序竞态**:

- `IProgress<T>`(若使用 `Progress<T>` 实现)的回调经由构造时捕获的
  `SynchronizationContext` 或线程池派发,是异步到达的;
- 若测试在 `RollbackAsync` 返回后立即断言回调次数/内容,高负载下回调可能尚未派发完成;
- 单独运行时线程池空闲,回调几乎总是"来得及",因此不复现。

## 建议排查方向

1. 阅读该测试对进度回调的收集与断言方式,确认是否存在
   "await 主操作 → 立即断言回调结果"的模式;
2. 若是,改为轮询等待条件(参照 `FileListViewModelTests.WaitForConditionAsync`
   的条件等待写法),或在测试中使用同步直调的 `IProgress<T>` 桩替代 `Progress<T>`;
3. 复现方法:循环运行完整套件(并行负载是触发条件),
   `1..20 | % { dotnet test --filter "FullyQualifiedName!~Integration" }`,
   统计失败频率验证修复。

## 关联

- 同日已修复的同类竞态:`FileListViewModelTests.GroupNode_IsSelectedTrue_UpdatesSelectedGroupAndFiltersFileList`
  (等待条件不充分,详见 [20260718-FileListViewModel测试基础设施遗留问题.md](20260718-FileListViewModel测试基础设施遗留问题.md))
