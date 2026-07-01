# 阶段性关键决策与细节控制文档 — B1 Everything 流式适配器

**任务编号**：B1
**任务概要**：Everything 流式适配器（扫描+排序下推）
**生成日期**：2026-06-21
**对应设计文档**：V3.7

---

## 一、关键架构/设计决策记录

### 1. SDK 可测试性抽象层

- **最终决策**：引入 `IEverythingSdk` 接口 + `EverythingSdkWrapper` 实现，将 EverythingNative 静态 P/Invoke 方法包装为可注入的接口。
- **核心权衡（Trade-off）**：增加了一个薄间接层（12 个方法一对一委托），牺牲了极少量运行时性能（虚方法调用开销），换取了 `EverythingFileListProvider` 的完全可单元测试能力。没有此抽象，核心流式枚举逻辑无法在无 Everything64.dll 的 CI 环境中验证。
- **被否决的替代方案**：
  1. **直接调用 EverythingNative 静态方法**：致命缺陷——不可测试，CI 环境无 Everything64.dll 导致全部测试跳过。
  2. **使用 Shim/Fakes 框架**：致命缺陷——需要 Visual Studio Enterprise 许可证，不可移植。

### 2. FileChanged 事件实现方式：轮询 vs 原生回调

- **最终决策**：使用 `System.Threading.Timer` 每 3 秒轮询 Everything `dm:>` 查询，非阻塞尝试获取 SDK 锁（`Wait(0)`），锁外触发事件。
- **核心权衡（Trade-off）**：3 秒延迟相对于 Everything 原生推送（近乎实时）有所增加，但换取了实现简洁性和可靠性——Everything SDK 不支持原生文件变更推送回调，自行实现 IPC 监听会引入复杂的命名管道逻辑和跨进程通信脆弱性。
- **被否决的替代方案**：
  1. **Everything IPC 命名管道监听**：致命缺陷——Everything 内部 IPC 协议为私有实现，无公开文档，随版本变化不稳定。
  2. **FileSystemWatcher 监听全盘**：致命缺陷——2M+ 文件的实时监控会导致句柄耗尽和 CPU 100%。

### 3. 墓碑缓存架构：双字典 FRN/指纹分离

- **最终决策**：`ITombstoneCache` 内部使用两个 `ConcurrentDictionary<string, LocalTombstone>`——`_frnCache`（键 `{VolumeGuid}:{FRN}`）和 `_fingerprintCache`（键 `{Size}:{LastWriteTime:O}`），通过 `FileIdentityKey` 是否包含日期时间 `T` 分隔符自动分流。
- **核心权衡（Trade-off）**：双字典占用双倍内存，但避免了每次查询时解析键格式的开销（热路径，`yield return` 中每条记录都查询）。
- **被否决的替代方案**：
  1. **单一 Dictionary + 键前缀区分**：致命缺陷——单字典需要字符串解析决定匹配策略，在 200 万文件枚举中积累为显著 CPU 开销。
  2. **每次查询 SQLite**：致命缺陷——`yield return` 循环中每次 I/O 会使扫描性能下降 100-1000 倍。

### 4. 枚举流程：锁内查询 + 锁外产出

- **最终决策**：`SemaphoreSlim(1,1)` 保护 Everything SDK 全局状态，查询执行在锁内，`yield return` 在锁外。`for (uint i = 0; i < resultCount; i++)` 逐索引通过 SDK 获取字段值，使用索引而非 Everything 内部游标。
- **核心权衡（Trade-off）**：索引访问方式意味着并发枚举时每个结果需要多次 SDK 调用（GetResultFullPathName、GetResultSize 等），但每个 SDK 调用是极快的 IPC 内存拷贝（微秒级），且此设计保证了结果集在枚举过程中的稳定性。
- **被否决的替代方案**：
  1. **在锁内 `yield return`**：致命缺陷——编译失败，C# 不允许在 try-finally 块内使用 `yield return`。
  2. **先全量获取路径再流式产出**：致命缺陷——2M 文件场景下 `List<string>` 路径列表本身占 >200MB 内存，违反 <200MB 约束。

---

## 二、隐含前提与关键假设清单

1. **假设 1**：Everything SDK 的 `Everything_GetNumResults()` 返回的结果数在 `for` 循环遍历过程中保持稳定（Everything SDK 内部结果集在锁释放后不会被其他操作覆盖）。若 Everything 在枚举过程中重建索引，可能导致索引越界——当前缓解措施：SDK 全局状态由 `SemaphoreSlim` 保护，轮询器在枚举进行中不会修改搜索条件。

2. **假设 2**：Everything_GetResultVolumeSerial() 返回的 32 位卷序列号在 NTFS 卷上是唯一的，且与 `Everything_GetResultFileReferenceNumber()` 组合可跨卷唯一标识文件。若在同一卷上存在重复序列号（非标准配置），FRN 墓碑匹配可能误命中。

3. **假设 3**：墓碑缓存容量 <10 万条。设计文档风险评估指出，若墓碑 >10 万条可能导致 `ConcurrentDictionary` 占用 >100MB。当前方案未实现 LRU 淘汰——若墓碑量超出此假设，需后续版本添加 `ITombstoneCache` 的 LRU 装饰器实现。

4. **假设 4**：`dm:>` 查询语法在 Everything ≥1.4.1 版本中可用且行为一致。若用户使用更旧版本，轮询静默失败（不触发事件），不影响核心扫描功能。

5. **假设 5**：路径缓冲区 4096 字符（~16KB Unicode）足够容纳所有 `\\?\` 扩展路径（NTFS 最大路径长度 32767 字符）。对于极端长路径（>4096 字符），路径将被截断，后续标准化将抛出 `ArgumentException` 并被静默跳过。

---

## 三、边界与范围界定

1. **明确不做**：B1 不实现 Everything 查询超时机制（设计文档 30s 超时）。当前 `Everything_Query(true)` 为同步阻塞调用，超时依赖 Everything 自身超时。此功能延后至集成测试阶段。

2. **明确不做**：B1 不实现墓碑缓存 LRU 淘汰策略。当前全量加载适用于墓碑量 <10 万条的预期场景。若墓碑超量，后续版本可在 `ITombstoneCache` 上添加装饰器实现。

3. **明确不做**：B1 不实现 Everything 变更事件的 Created/Deleted/Renamed 区分（轮询仅能检测文件存在性）。`FileChanged` 当前统一标记为 `Modified`，详细的变更类型区分延后至 C2 阶段。

4. **明确不做**：B1 不处理 Everything 意外退出后的自动重连。若 Everything 进程在枚举过程中退出，调用方将收到 `EverythingSdkException`，UI 层负责展示错误和处理重试。

---

## 四、待确认事项

1. **待确认项 A**：`Everything_GetResultDateModified` 返回的 FILETIME 是否总是 UTC——代码中使用 `DateTime.FromFileTimeUtc()` 转换，若 Everything SDK 在某些配置下返回本地时间的 FILETIME，时间戳将偏差。需在 Everything ≥1.4.1 + Windows 11 环境下验证。

2. **待确认项 B**：轮询间隔 3 秒是否为可接受的延迟——设计文档仅要求"数秒内"，但 UX 团队可能期望更快的响应（如 1 秒）。`ChangePollIntervalMs` 常量可在集成测试后调整，但降低间隔会增加 Everything IPC 负载。

3. **待确认项 C**：`TombstoneCache.IsTombstoned` 在 FRN 键和指纹键均非 null 时的优先级——当前实现优先查 FRN 缓存，若 FRN 未命中再查指纹。这是合理的默认行为，但若存在 FRN 键构造失败而指纹键可用的情况，可能存在边界行为差异。

---

## 五、关键外部依赖锁定

- **依赖项 1**：`Everything64.dll` ≥ 1.4.1.1000（运行时依赖）——若用户未安装或版本过低，`EverythingDependencyDetector`（A2）将在启动时阻断。B1 流式适配器仅在所有检测通过后才可用。

- **依赖项 2**：`ILocalTombstoneRepository.GetAllAsync()`（A3）——墓碑缓存的启动加载依赖此方法的实现。若 SQLite 表不存在或磁盘损坏，`LoadAsync` 将抛出异常并阻止文件枚举。

- **依赖项 3**：`PathNormalizer.Normalize()`（A5）——所有路径标准化依赖此静态方法。若路径格式异常导致高频抛出 `ArgumentException`，枚举将跳过这些文件（静默降级）。

- **依赖项 4**：`IConfigService.Current.FRN_AVAILABLE`（A4）——FRN 能力标记由 `EverythingDependencyDetector` 在启动时写入。若配置未正确加载或 FRN 探测未执行，默认值 `true` 可能导致 FRN 键构造后匹配失败（非致命，但墓碑过滤精度下降）。
