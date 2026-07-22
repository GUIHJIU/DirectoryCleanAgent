# Everything 索引为空导致集成测试失败

- **发现日期**: 2026-07-19
- **状态**: 待处理
- **优先级**: 中（需要 Everything 真实环境的测试无法在 CI/无索引机器上通过）

## 现象

`EverythingScanIntegrationTests` 中全部 5 个 `[EverythingFact]` 测试失败，尽管 `EverythingTestHelper.IsAvailable` 返回 `true`（进程运行 + DLL 版本检测通过），但 `EnumerateFilesAsync` 返回 0 条结果：

| 测试 | 断言 | 实际 |
|------|------|------|
| `EnumerateFilesAsync_RealEverything_ReturnsFiles` | `Assert.NotEmpty(files)` | 集合为空 |
| `EnumerateFilesAsync_RealEverything_PathFilterWorks` | `Assert.NotEmpty(files)` | 集合为空 |
| `EnumerateFilesAsync_RealEverything_FileItemFieldsPopulated` | `Assert.NotEmpty(files)` | 集合为空 |
| `EnumerateFilesAsync_RealEverything_CancellationStopsStream` | `Assert.Throws<OperationCanceledException>()` | 未抛出（级联：无数据可取消） |
| `EnumerateFilesAsync_WithSearchExpression_FiltersResults` | `Assert.NotEmpty(files)` | 集合为空 |

## 根因分析

`EverythingTestHelper.IsAvailable` 的检测逻辑仅做两层轻量检查：
1. **进程存活** — `Process.GetProcessesByName("Everything")` 非空
2. **DLL 版本** — `Everything_GetMajorVersion()` 返回非零

这两项通过后即认为 Everything 可用。但实际情况可能：
- Everything.exe 正在运行，但**未配置任何索引卷**（NTFS 卷未勾选）
- 索引**正在构建中**尚未完成（`IsDBLoaded()` 返回 false，但 `IsAvailable` 不检查此状态）
- Everything 版本与 SDK 存在**兼容性问题**（版本号可获取但搜索 IPC 协议不匹配）

测试无过滤条件查询（`SearchExpression=""`，匹配全部文件）却返回 0 条结果，说明 Everything 索引数据库确实为空。

## 影响

- 所有依赖真实 Everything 索引的集成测试（5 个）在无完整索引的机器上**必然失败**
- `Assert.Throws<OperationCanceledException>` 测试依赖"流足够长才能触发取消"，在索引为空时成为**级联失败**
- `EverythingTestHelper` 的检测不够充分：通过检测 ≠ 索引可用

## 建议处理方向

1. **增强 `EverythingTestHelper.IsAvailable` 检测**：在现有两层检查后增加第三步 — 执行一次轻量 IPC 查询（如 `Everything_SetSearch("")` + `Everything_Query(true)` + `Everything_GetNumResults() > 0`），确保索引非空才返回 `true`
2. **CI 环境跳过**：为 `[EverythingFact]` 增加 trait 过滤，CI 流水线默认排除，仅在有 Everything 环境的专用机器上运行
3. **取消测试解耦**：将 `CancellationStopsStream` 测试与真实 Everything 解耦，使用 `Mock<IFileListProvider>` 注入可控的慢速流，确保取消窗口确定存在

## 环境备注

2026-07-19 测试机 Everything 进程正在运行但索引为空/未完成。执行 `Everything_SetSearch("")` + `Everything_Query(true)` 后 `Everything_GetNumResults()` 返回 0。
