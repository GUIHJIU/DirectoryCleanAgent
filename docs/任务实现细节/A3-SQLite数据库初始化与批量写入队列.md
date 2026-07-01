# A3: SQLite 数据库初始化与批量写入队列 — 实现审计报告

**审计日期**: 2026-06-21
**审计范围**: 18 个新增/修改文件（2 DTO + 12 Data 层 + 4 测试）
**编译状态**: ✅ 0 Error, 2 Warning
**测试状态**: ✅ 97 Passed (53 已有 + 44 新增), 0 Failed

---

## 一、关键权衡

| # | 权衡点 | 选择的方案 | 放弃的方案 | 影响 | 来源 |
|---|--------|-----------|-----------|------|------|
| 1 | 批量队列失败处理 | 异常时条目退回 ConcurrentQueue 重新入队 | 丢弃失败条目并记录日志 | 保证数据不丢失，但可能造成重复写入（INSERT OR REPLACE/IGNORE 幂等兜底） | `BatchWriteQueue.cs:128-138` — 失败回退逻辑 |
| 2 | DeletionRecord 压缩阈值 | JSON > 4KB 时 GZip + Base64，以 `GZ:` 前缀标记 | 直接存 JSON 或统一压缩 | >4KB 阈值对应设计文档 5.1 节；小快照可人读，大快照省空间 | `DeletionRecordRepository.cs:27-29` — CompressThresholdBytes 常量 |
| 3 | 墓碑插入冲突策略 | `INSERT OR IGNORE`（主键冲突静默跳过） | `INSERT OR REPLACE` | 同一物理文件被多次删除时不会覆盖墓碑，保留首次删除信息用于精确回滚 | `LocalTombstoneRepository.cs:105` |
| 4 | 回滚后墓碑清除 | 直接执行 DELETE（不入队），先 Flush 再删 | 入队异步删除 | 回滚后必须立即清除墓碑，否则后续扫描会错误过滤已恢复文件 | `LocalTombstoneRepository.cs:79-81` — DeleteByOperationIdAsync 先 Flush |
| 5 | DeletionMethod 存储格式 | 数据库中存 TEXT（`RECYCLE_BIN`/`PERMANENT`/`QUARANTINE`） | 存枚举 int | 兼容设计文档中 `QUARANTINE` 值（当前 DeleteMethod 枚举尚未包含） | `DeletionRecordRepository.cs:295-301` — ParseDeleteMethod 映射 |
| 6 | 连接缓存策略 | `Cache=Private`（每连接独立缓存） | `Shared` 共享缓存 | WAL 模式下 Private 避免多线程缓存竞争，且每个 Flush 使用独立连接+事务 | `DatabaseInitializer.cs:41` — SqliteCacheMode.Private |

---

## 二、隐含假设

| # | 假设内容 | 风险 | 来源 |
|---|---------|------|------|
| 1 | 假设 SQLite 文件所在目录已存在或可创建 | 若 `%LocalAppData%\SpaceClear` 权限不足，Directory.CreateDirectory 会抛异常 | `DatabaseInitializer.cs:80-85` — EnsureDirectoryExists()；AI 推测设计者预期此路径在普通用户权限下可写 |
| 2 | 假设 Dispose 时 Flush 可在 5 秒内完成 | 大批量数据落盘超 5 秒时，后台任务 Wait(5s) 后可能仍有未落盘数据 | `BatchWriteQueue.cs:156` — `_backgroundTask.Wait(TimeSpan.FromSeconds(5))` |
| 3 | 假设调用方在应用退出前逐一 Dispose 仓储 | 若 DI 容器未正确释放，后台轮询 Task 可能被强行终止导致队列中数据丢失 | `BatchWriteQueue.cs:145-170` — Dispose 中的最后 Flush |
| 4 | 假设 file_path 已由调用方标准化为 `\\?\` 格式 | 仓储层不做路径格式校验，混入非标路径会导致墓碑匹配失败 | `FileDecisionCacheRepository.cs` — 直接存储 file_path 无转换 |
| 5 | 假设 DeletionRecord.DecisionSnapshotJson 由 IDecisionEngine 产出（AI 推测） | 若调用方传入非 JSON 字符串，解压/查询时不会报错但语义错误 | `DeletionRecordRepository.cs:28-35` — 只做长度判断不做 JSON Schema 校验 |
| 6 | 假设单次 Flush 批次不会超过 SQLite 参数上限 | SQLite 默认 `SQLITE_MAX_VARIABLE_NUMBER=999`，200 条 × 7~11 列=1400~2200 参数，可能超限 | `BatchWriteQueue.cs:18` — batchSize 默认 200；这可能是潜在 Bug，**建议确认** |

---

## 三、明确不做

| # | 不做的事项 | 代码证据 |
|---|----------|---------|
| 1 | 审计日志不支持 Update/Delete | `IAuditLogRepository` 仅暴露 `Insert`、`QueryAsync`、`FlushAsync`，无任何修改/删除方法；测试用例 `IAuditLogRepository_ShouldNotExposeUpdateOrDelete` 用反射二次验证 |
| 2 | 仓储不做路径格式校验 | 所有 Repository 直接存储 `file_path`，无 `\\?\` 前缀检查或转换逻辑 |
| 3 | 批量队列不做优先级排序 | ConcurrentQueue 严格 FIFO，无优先级通道 |
| 4 | DatabaseInitializer 不做迁移版本管理 | 无 Migration 版本号表，所有 DDL 依赖 `IF NOT EXISTS` 幂等；若要加列需手动 ALTER |
| 5 | 不做连接池/连接复用 | 每次查询和 Flush 都新建 SqliteConnection（`CreateConnectionAsync`），用完即 `Dispose`；依赖 ADO.NET 连接池 |
| 6 | 不限制 AuditLog 表大小 | 无自动清理机制或行数上限；永续增长 |

---

## 四、待确认事项

| # | 事项 | 为何需要确认 | 建议引导位置 |
|---|------|------------|-------------|
| 1 | SQLite 参数上限问题 | 200 条 × 最多 11 列（FileDecisionCache）= 2200 参数，超过默认 999；可能需降 batchSize 或分批执行 | 设计文档 5.2 节 — 仅写了"200 条"，未讨论 SQLite 参数限制；需确认是每个表独立 200 还是全局 200 |
| 2 | `DeleteMethod` 枚举是否要扩展 `Quarantine` | 当前枚举仅有 `RecycleBin`/`Permanent`，但 DeletionRecord 表存储文本 `QUARANTINE`；`ParseDeleteMethod` 将其映射为 `Permanent` | `DeleteMethod.cs` 枚举定义；`DeletionRecordRepository.cs:295-301` 映射逻辑 |
| 3 | `OperationBatchInfo` 定义在 `IBackupManager.cs` 而非独立文件 | 删除记录仓储依赖回滚管理器的接口类型，形成隐式耦合 | `IDeletionRecordRepository.cs:4` — `using DirectoryCleanAgent.Core.Interfaces` |
| 4 | 压缩的 `GZ:` 前缀是否与设计文档约定的格式一致 | 设计文档 5.1 节提到"GZip 压缩后 Base64 编码存储"，但未定义压缩标记格式 | `DeletionRecordRepository.cs:30` — `CompressionPrefix = "GZ:"`；此为 AI 自行设计的标记格式 |
| 5 | 后台轮询间隔 50ms 是否过短 | 刷新周期 500ms 内以 50ms 粒度检查取消，可能带来不必要的 CPU 唤醒 | `BatchWriteQueue.cs:22` — `PollInterval = 50ms`；生产环境可考虑放宽至 100-250ms |
| 6 | DatabasePath 目录创建时机 | `EnsureDirectoryExists` 在 `InitializeAsync` 中调用，若路径由 `IConfigService` 决定但 Config 尚未加载会失败 | `DatabaseInitializer.cs:80`；调用方 `InitializeDatabaseAsync` 在 `ServiceRegistration.cs:71` |

---

## 五、外部依赖

| # | 依赖项 | 类型 | 版本 | 用途 | 风险 |
|---|-------|------|------|------|------|
| 1 | `Microsoft.Data.Sqlite` | NuGet 包 | 8.0.11 | SQLite ADO.NET 提供程序 | 间接依赖 `SQLitePCLRaw.lib.e_sqlite3` 原生库，在受限网络环境（中国 GFW）可能下载失败 |
| 2 | `Microsoft.Extensions.Logging` | NuGet 包 | 8.0.1 | 结构化日志（已由 Core 项目引入） | 仓储层未强制日志级别，生产环境 `Debug` 级别会输出大量批量写入日志 |
| 3 | `System.IO.Compression` | .NET BCL | —（框架内置） | GZip 压缩 decision_snapshot | 无风险 |
| 4 | `System.Text.Json` | .NET BCL | —（框架内置） | DecisionSnapshot JSON 序列化（由调用方使用，仓储层未直接依赖） | 无风险 |
| 5 | SQLite 原生库（`e_sqlite3.dll`） | 原生二进制 | ≥ 3.39.0（WAL 稳定） | 实际数据库引擎 | 部署时需确保原生库随应用分发；`Microsoft.Data.Sqlite` 通过 `SQLitePCLRaw` bundle 自动处理但依赖网络下载 |
| 6 | `DirectoryCleanAgent.Core` | 项目引用 | — | 依赖 DTO、枚举、`OperationBatchInfo` | `OperationBatchInfo` 定义在 `IBackupManager.cs` 中而非独立 DTO 文件，形成耦合（见待确认事项 #3） |

---

## 补充：潜在风险项的优先级评估

| 优先级 | 事项 | 理由 |
|--------|------|------|
| **P0** | SQLite 参数上限（待确认 #1） | 可能导致运行时批量写入崩溃，是硬错误 |
| **P1** | `GZ:` 压缩标记格式（待确认 #4） | 若与后续 B5/B6 回滚模块不一致，已存数据无法正确解压 |
| **P2** | `OperationBatchInfo` 耦合（待确认 #3） | 重构 `IBackupManager` 时可能引起 Data 层编译错误 |
| **P3** | 轮询间隔 50ms（待确认 #5） | 低风险，仅影响笔记本电池续航 |
