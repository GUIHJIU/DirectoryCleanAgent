# A4 — 配置管理（JSON + 热加载基础设施）阶段性关键决策与细节控制文档

**版本**: 1.0
**日期**: 2026-06-21
**状态**: 已实现，全量 109 测试通过

---

## 一、 关键架构/设计决策记录

### 决策 1：防抖实现 — `System.Threading.Timer`（而非 `Task.Delay` + `CancellationTokenSource`）

- **最终决策**：使用 `System.Threading.Timer` 的 `Change(dueTime, Timeout.Infinite)` 单次触发模式实现 500ms 防抖。每次 FSW 事件到达时 `Change()` 重置计时器，计时器到期后回调 `IncrementRuleCacheVersion()`。
- **核心权衡**：牺牲了 `Task.Delay`+CTS 模式的 async/await 可读性，换取零分配（无 Task 创建开销）和精确的单次触发语义（Timer 的 period=Infinite 保证只触发一次，不会因竞态导致多次触发）。
- **被否决的替代方案**：
  - *方案 A*：`Task.Delay` + `CancellationTokenSource` — **致命缺陷**：每次 FSW 事件都需要 Cancel + Dispose + 重建 CTS，高频率变更时 CTS 分配开销大，且 CTS.Dispose 非线程安全易出 bug。
  - *方案 B*：`System.Timers.Timer` — **致命缺陷**：该 Timer 是 Windows Forms 时代的组件，默认携带 `SynchronizingObject` 试图封送回调到 UI 线程，在纯 ThreadPool 场景下是多余的负担；且 `AutoReset=false` 无法做到无竞态的单次重置。

### 决策 2：Watcher 与 ConfigService 的耦合方式 — 直接依赖（而非事件中介）

- **最终决策**：`RulesDirectoryWatcher` 构造函数直接注入 `IConfigService`，防抖回调中直接调用 `IncrementRuleCacheVersion()`。
- **核心权衡**：牺牲了 Watcher 的通用性（它不能监控任意目录做任意事），换取极简的调用链和零额外分配。此 Watcher 的职责在需求文档中明确是"监控规则目录→递增版本号"，不需要通用性。
- **被否决的替代方案**：
  - *方案 A*：Watcher 暴露 `FileChanged` 事件，由协调器订阅后调用 `IConfigService` — **致命缺陷**：增加无意义的中间层，协调器引入额外生命周期管理复杂度，且在设计文档中无此架构需求。

### 决策 3：`ConfigChanged` 事件在锁外触发

- **最终决策**：`IncrementRuleCacheVersion` 在 SemaphoreSlim 内部完成递增+持久化后，Release 锁，然后在锁外调用 `OnConfigChanged()`。
- **核心权衡**：若有事件处理器回调 `IConfigService.SaveAsync()` 会导致重入等待（SemaphoreSlim 不支持重入），造成死锁。锁外触发避免了这一问题，代价是事件处理器看到的配置状态可能与刚持久化的状态之间有一个微小的时间窗口（其他线程可能在此期间又修改了配置）。
- **被否决的替代方案**：
  - *方案 A*：使用 `SemaphoreSlim` 的 `WaitAsync(0)` 检测重入 — **致命缺陷**：仅检测到重入但无法处理，仍然需要抛出或跳过，不如从架构上规避。

### 决策 4：`JsonConfigService.IncrementRuleCacheVersion()` 竞态条件修复 — SemaphoreSlim 内完成递增+保存

- **最终决策**：将递增操作移入 SemaphoreSlim 内部，与 `SaveInternalAsync` 在同一个锁保护区域内执行，保证版本号递增和持久化是原子操作。
- **核心权衡**：`IncrementRuleCacheVersion` 使用 fire-and-forget (`Task.Run`) 不阻塞调用方（FSW 事件线程），代价是调用方无法直接感知保存是否成功（仅通过日志记录错误）。同步等待会阻塞 FSW 内部线程，可能导致后续事件丢失。
- **被否决的替代方案**：
  - *方案 A*：使用 `Interlocked.Increment` 仅递增内存值，延迟保存 — **致命缺陷**：若进程在保存前崩溃，版本号丢失，重启后 `RuleCacheVersion` 与实际规则状态不一致。

---

## 二、 隐含前提与关键假设清单

1. **[假设 1]**：`%LocalAppData%\SpaceClear\rules\` 目录仅包含 `.json` 规则文件，不含需要递归监控的子目录。若未来规则文件需要按子目录组织（如 `rules/system/`、`rules/user/`），当前的 `IncludeSubdirectories = false` 将无法监控子目录变更。
2. **[假设 2]**：规则文件的变更频率较低（人工编辑或内置编辑器保存），不会触发 `FileSystemWatcher` 内部缓冲区溢出（64KB 缓冲区可容纳约 1000+ 事件）。若未来引入批量规则导入功能，可能触发 Error 事件路径。
3. **[假设 3]**：`System.Threading.Timer` 回调在 ThreadPool 上执行，假设 `IConfigService.IncrementRuleCacheVersion()` 不会长时间阻塞（内部仅有内存递增 + JSON 序列化 + 文件写入，耗时 <100ms）。
4. **[假设 4]**：宿主应用在退出时会按正确顺序 Dispose 服务（先 `RulesDirectoryWatcher.Dispose()`，再 `JsonConfigService.Dispose()`）。若顺序颠倒，防抖回调可能在 ConfigService 已释放后触发，导致 ObjectDisposedException。
5. **[假设 5]**：`UserConfig.Clone()` 使用 `MemberwiseClone()` + 手动深拷贝 `List<string>` 字段。此方案假设 UserConfig 的所有引用类型字段都是 `string`（不可变）或 `List<string>`（需深拷贝）。若未来新增其他可变引用类型字段（如 `Dictionary`、嵌套对象），Clone 方法需要同步更新。

---

## 三、 边界与范围界定

1. **[明确不做]**：本期不实现递归子目录监控。设计文档 6.5 节仅指定监控规则目录本身，`IncludeSubdirectories = false`。
2. **[明确不做]**：本期不实现防抖回调的取消令牌传递（`CancellationToken` 不传入 `IncrementRuleCacheVersion`），因为版本递增是轻量操作且不应被取消。
3. **[明确不做]**：本期不实现规则文件内容的校验（如 JSON Schema 验证）。`RulesDirectoryWatcher` 只检测文件变更事件，不解析文件内容。内容校验属于 B2 规则引擎阶段。
4. **[明确不做]**：本期不实现多个规则目录的并发监控。若未来需要同时监控用户规则目录和系统规则目录，需扩展 IRulesDirectoryWatcher 或创建多个实例。
5. **[明确不做]**：本期不实现 `IConfigService.Current` 的快照保护（返回只读副本）。当前 `Current` 属性返回可变引用，调用方应通过 `UserConfig.Clone()` 获取安全快照。此设计保持简单，由调用方负责正确使用。

---

## 四、 待确认事项

1. **[待确认项 A]**：`FileSystemWatcher.InternalBufferSize` 当前设为 64KB。若用户的规则目录位于高延迟网络存储（如 NAS），可能需要调整。当前值基于本地 NTFS 磁盘的默认场景。
2. **[待确认项 B]**：防抖间隔默认 500ms（与设计文档一致）。若用户反馈"保存规则后界面刷新有延迟感"，可将此值调小至 200-300ms。当前值偏向稳健，牺牲了少许响应速度。
3. **[待确认项 C]**：`Error` 事件中的重启逻辑使用了即时重建（在 `_lock` 内调用 `CleanupInternal` 然后重新创建 FSW）。若 `Error` 事件频繁触发（如文件系统异常），理论上可能导致 CPU 空转。当前假设 Error 是罕见事件，未加入退避策略。若需要，后续可在 `RulesDirectoryWatcher` 中加入指数退避（如 1s → 2s → 4s → 最多 30s 重启延迟）。
4. **[待确认项 D]**：测试中的 `ConfigServiceSpy` 未实现完整的 `IConfigService` 语义（如 `SaveAsync` 为空操作）。当前仅用于验证调用次数和时序，若未来需要验证保存行为的测试，需扩展 Spy 或引入 Moq。

---

## 五、 关键外部依赖锁定

| 依赖项 | 类型 | 状态 | 阻塞风险 |
|--------|------|------|----------|
| `Microsoft.Extensions.Logging` | NuGet 包 | 已引入 | 无 |
| `Microsoft.Extensions.DependencyInjection` | NuGet 包 | 已引入 | 无 |
| `System.IO.FileSystem.Watcher` | .NET 8 框架内置 | 可用 | 无 |
| `System.Threading.Timer` | .NET 8 框架内置 | 可用 | 无 |
| `%LocalAppData%\SpaceClear\` 目录写入权限 | 运行环境 | 由 JsonConfigService 首次 SaveAsync 创建 | 若企业组策略限制 AppData 写入，配置服务和热加载均不可用（环境问题，非代码防御范围） |
| Everything SDK ≥ 1.4.1.1000 | 外部进程 | Phase A2 已实现检测 | 若 Everything 不可用，工具整体阻断，但配置服务本身不依赖 Everything |

---

## 六、 变更清单

| 操作 | 文件 | 说明 |
|------|------|------|
| 修改 | `src/DirectoryCleanAgent.Core/Config/JsonConfigService.cs` | 修复 `IncrementRuleCacheVersion()` 竞态条件：递增+保存移入 SemaphoreSlim 内部，ConfigChanged 在锁外触发 |
| 修改 | `src/DirectoryCleanAgent.Core/Config/UserConfig.cs` | 新增 `Clone()` 深拷贝方法 |
| 新建 | `src/DirectoryCleanAgent.Core/Config/IRulesDirectoryWatcher.cs` | 规则目录监控器接口抽象 |
| 新建 | `src/DirectoryCleanAgent.Core/Config/RulesDirectoryWatcher.cs` | FileSystemWatcher + Timer 500ms 防抖实现 |
| 新建 | `src/DirectoryCleanAgent.Core/Config/ServiceRegistration.cs` | DI 扩展方法 `AddConfigLayer()` |
| 修改 | `src/DirectoryCleanAgent.Tests/Core/Config/JsonConfigServiceTests.cs` | 新增 2 个测试：并发竞态验证、ConfigChanged 事件 |
| 新建 | `src/DirectoryCleanAgent.Tests/Core/Config/RulesDirectoryWatcherTests.cs` | 新建 9 个测试：目录创建、防抖合并、文件过滤、Stop/Dispose、重启、双次 Dispose、运行中改参数 |
| 修改 | `src/DirectoryCleanAgent.Tests/Core/Config/UserConfigTests.cs` | 新增 Clone 独立性测试 |

**测试结果**：全量 109 测试通过，0 失败，0 跳过。
