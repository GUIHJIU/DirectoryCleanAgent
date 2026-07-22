# 一键清理安全项 — 缓存复用方案设计

**日期**: 2026-07-18
**状态**: 草稿
**范围**: `CollectCandidatesAsync` 缓存感知改造 + `IFileDecisionCacheRepository` 新增按 Action+Version 组合查询接口

---

## 1. 背景与现状分析

### 1.1 当前数据流

```
ExecuteSimulate() / ExecuteRefresh()
  +--> SimulationService.SimulateAsync()
         +--> _cacheRepo.ClearAsync()         // 清空旧缓存
         +--> 遍历 Everything SDK              // B1: 全量扫描
         |     +--> B2: RuleEngine.Evaluate
         |     +--> B3: DecisionEngine.Arbitrate
         |     +--> _cacheRepo.Upsert(cache)     // 写入缓存（每文件）
         +--> _cacheRepo.FlushAsync()            // 刷新队列

ExecuteQuickClean() / ExecuteSmartSelect()
  +--> CollectCandidatesAsync(targetAction)
        +--> 遍历 Everything SDK              // B1: 全量扫描 <-- 重复扫描，无视缓存
        |     +--> B2: RuleEngine.Evaluate
        |     +--> B3: DecisionEngine.Arbitrate
        +--> 返回匹配 targetAction 的候选列表
        // 不写入缓存，下次点击仍然全量扫描
```

### 1.2 性能问题量化

- **100 万文件扫描 + B2/B3 评估**: 当前 ~8-15 秒；缓存命中后 <500ms (SQLite 查询)
- **Windows 系统文件 (Protected)**: 当前占比 ~60-80%，每次重新扫描丢弃；缓存命中后不扫描
- **点击"一键清理"时**: 当前每次都全量扫描；改造后首次之后不再扫描

### 1.3 已有基础设施

- `MainViewModel._cacheRepo` (IFileDecisionCacheRepository) 在构造函数第 61、96 行已注入 -- **无需新增依赖**
- `FileDecisionCache.CacheVersion` 字段可与 `UserConfig.RuleCacheVersion` 比对
- IFileDecisionCacheRepository 提供 `GetByActionAsync(FinalAction)` 和 `GetByVersionAsync(int)`
- `SimulationService.SimulateAsync` 扫描期间已通过 `_cacheRepo.Upsert(cache)` 写入缓存，结束后 `FlushAsync`
- `FileListViewModel.LoadDataAsync` 已从缓存读取

---

## 2. 改动范围总览

### 2.1 需修改的文件

| 文件 | 改动类型 | 说明 |
|------|----------|------|
| `src/DirectoryCleanAgent.Data/IFileDecisionCacheRepository.cs` | 修改 | 新增 `GetByActionAndVersionAsync` 方法签名 |
| `src/DirectoryCleanAgent.Data/FileDecisionCacheRepository.cs` | 修改 | 实现新方法（SQL 组合查询） |
| `src/DirectoryCleanAgent/ViewModels/MainViewModel.cs` | 修改 | `CollectCandidatesAsync` 缓存感知改造 |

### 2.2 不需要修改的内容

- **构造函数依赖注入**: `MainViewModel` 已有 `_cacheRepo` 字段，无需新增
- **`CollectCandidatesAsync` 方法签名**: 保持 `Task<List<FileDecisionCache>> CollectCandidatesAsync(FinalAction targetAction, CancellationToken ct)` 不变
- **ISimulationService 接口**: 缓存写入逻辑不变
- **FileListViewModel**: 读取路径不变（仍用 `GetAllAsync()`）
- **DI 容器注册**: 无变化

---

## 3. 接口层改动

### 3.1 IFileDecisionCacheRepository 新增方法

```csharp
/// <summary>
/// 按 FinalAction + CacheVersion 组合查询缓存记录。
/// 用于快速判断缓存是否有效且包含目标操作的文件：
///   空结果 = 缓存空 或 版本过期，需全量扫描
///   非空结果 = 缓存命中，直接返回
/// </summary>
Task<IReadOnlyList<FileDecisionCache>> GetByActionAndVersionAsync(
    FinalAction action, int cacheVersion, CancellationToken ct = default);
```

**设计理由**: 相比"查 ByAction 后在内存中逐条校验 version"，组合查询将过滤下推到 SQLite，避免传输不必要记录，只需一轮数据库往返。

### 3.2 FileDecisionCacheRepository 实现

```csharp
public async Task<IReadOnlyList<FileDecisionCache>> GetByActionAndVersionAsync(
    FinalAction action, int cacheVersion, CancellationToken ct = default)
{
    const string sql = """
        SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
               ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
        FROM FileDecisionCache
        WHERE final_action = @action AND cache_version = @version
        """;

    await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@action", (int)action);
    cmd.Parameters.AddWithValue("@version", cacheVersion);

    var results = new List<FileDecisionCache>();
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
        results.Add(MapFromReader(reader));
    }

    return results.AsReadOnly();
}
```

---

## 4. 核心逻辑改造：CollectCandidatesAsync

### 4.1 控制流

```
CollectCandidatesAsync(targetAction, ct)
|
+-- 1. 缓存优先读取
|   +-- config = _configService.Current  // 捕获配置快照，全流程使用
|   +-- await _cacheRepo.FlushAsync(ct)  // 刷新队列，确保 AI 写入可见
|   +-- cached = await _cacheRepo.GetByActionAndVersionAsync(
|   |       targetAction, config.RuleCacheVersion, ct)
|   +-- if (cached.Count > 0)
|   |   +-- _logger.LogInformation("[缓存命中] 返回 {Count} 条, v{Version}",
|   |   |       cached.Count, config.RuleCacheVersion)
|   |   +-- return cached.ToList()
|   +-- else
|   |   +-- _logger.LogInformation("[缓存未命中] Action={Action}, v{Version}, 降级全量扫描",
|   |           targetAction, config.RuleCacheVersion)
|   +-- goto 全量扫描
|
+-- 2. 全量扫描（兜底）
|   +-- 使用步骤1捕获的 config 快照确保原子性
|   +-- 遍历 Everything SDK (B1)
|   |   +-- B2: RuleEngine.Evaluate
|   |   +-- B3: DecisionEngine.Arbitrate
|   |   +-- _cacheRepo.Upsert(arbitrated)     // 写入所有规则命中的文件
|   |   +-- if (arbitrated.FinalAction == targetAction)
|   |       +-- candidates.Add(arbitrated)
|   +-- await _cacheRepo.FlushAsync(ct)
|
+-- 3. return candidates
```

### 4.2 设计决策：兜底时不清空缓存

与 `SimulationService.SimulateAsync` 不同，`CollectCandidatesAsync` 在兜底扫描时**不清空**已有缓存:

| 对比项 | SimulateAsync | CollectCandidatesAsync（兜底） |
|--------|---------------|-------------------------------|
| 先 ClearAsync | 是（需重建完整缓存） | 否 |
| 写入 | Upsert 所有规则命中文件 | Upsert 所有规则命中文件 |
| 效果 | 清除旧 scope 残留数据 | 保留旧 scope 数据 + 合并新 scope 数据 |
| 理由 | 模拟运行应反映最新扫描范围 | QuickClean 仅关心目标操作文件，最小侵入；已通过版本号确保一致性 |

**权衡**: 不清空缓存意味着若用户切换扫描范围（IncludedVolumes）后未重新模拟运行，缓存中可能残留旧扫描范围的文件。此场景下用户应优先执行"刷新扫描"而非直接"一键清理"。

---

## 5. 版本校验策略

### 5.1 校验机制

版本校验完全委托给 SQL `WHERE cache_version = @version` 子句。`CacheVersion` 源自 `UserConfig.RuleCacheVersion`，该值在以下情况递增：

- 规则文件变更（FileSystemWatcher + 500ms 防抖）
- 用户通过内置编辑器手动修改规则
- 应用启动时自动递增（若检测到规则文件时间戳变化）

规则版本未变 → 缓存命中；规则版本已变 → 缓存 miss，全量扫描并写入新版本。

### 5.2 无需额外校验

不需要在内存中逐条验证 DTO 的 `CacheVersion`，因为 `GetByActionAndVersionAsync` 的 SQL 已按版本过滤，且 `SimulateAsync` 全量写入时使用统一的 `config.RuleCacheVersion`。

---

## 6. 缓存写入策略

### 6.1 写入范围

写入所有**规则命中**（`RuleVerdict != Keep`）的文件，不限其 `FinalAction`:

| FinalAction | 写入缓存 | 用途 |
|---|---|---|
| AutoDelete | 是 | QuickClean（一键清理）复用 |
| SuggestDelete | 是 | SmartSelect（智能选择）复用 |
| ManualReview | 是 | 仪表板统计、文件列表显示 |
| Protected | 是 | 仪表板统计、文件列表显示 |

### 6.2 Flush 时机

遍历结束后统一调用 `await _cacheRepo.FlushAsync(ct)`。不逐文件 flush，因为 `BatchWriteQueue` 内部每 500ms 或 200 条自动 flush。遍历结束后的显式 flush 确保队列清空，下次读取能立即命中。

---

## 7. 并发安全

### 7.1 现有防护

- `IsOperating` 状态阻止按钮重复点击
- `_currentCts` 管理操作取消
- SQLite WAL 模式支持并发读写

### 7.2 剩余风险与处理

| 场景 | 风险 | 处理 |
|---|---|---|
| SimulateAsync 进行中，缓存正在清除 | QuickClean 读到空缓存 | 自动触发全量扫描（正确行为，虽效率低但安全） |
| QuickClean 全量扫描写入时，SimulateAsync 开始 | SimulateAsync 中的 ClearAsync 会清空刚刚写入的数据 | IsOperating 阻止此场景（按钮不可点击） |
| AI 分析完成更新缓存后，QuickClean 读取 | 读取 AI 更新后的决策 | 正确行为：AI 标签通过 AiAnalysisCoordinator 直接更新缓存条目 |

### 7.3 队列清空保证

`ClearAsync` 实现在 SQL DELETE 前调用了 `_writeQueue.FlushAsync(ct)`，确保清空前所有待写入数据已落盘。QuickClean 兜底扫描的写入经过同一个 `BatchWriteQueue`，`FlushAsync` 在遍历结束后调用，不会与 `ClearAsync` 产生冲突。

---

## 8. 日志与错误处理

### 8.1 日志事件

| 事件 | 日志级别 | 消息内容 |
|---|---|---|
| 缓存命中 | Information | `[缓存命中] CollectCandidatesAsync({Action}) 从缓存返回 {Count} 条, CacheVersion={Version}` |
| 缓存未命中 | Information | `[缓存未命中] CollectCandidatesAsync({Action}) 缓存为空或版本过期, 开始全量扫描` |
| 全量扫描完成 | Information | `[缓存写入] CollectCandidatesAsync 全量扫描完成: 处理={Processed}, 写入缓存={Upserted}, 候选={Candidates}` |
| 缓存读取异常 | Warning | `[缓存读取异常] GetByActionAndVersionAsync 失败, 降级为全量扫描: {Exception}` |
| 缓存写入失败 | Warning | `[缓存写入] FlushAsync 失败（非致命），候选文件已就绪` |

### 8.2 错误兜底策略

**原则**: 缓存读失败时降级为全量扫描（与当前行为一致），不阻塞用户操作。

- 缓存读取异常 → `cached = Array.Empty<FileDecisionCache>()`，触发全量扫描
- 缓存写入失败 → 记录 Warning 日志，不阻塞候选文件返回

---

## 9. 向后兼容

| 场景 | 改造前行为 | 改造后行为 | 是否一致 |
|---|---|---|---|
| 首次使用（缓存表为空） | 全量扫描 | `GetByActionAndVersionAsync` 返回空 → 全量扫描 | 一致 |
| 已执行模拟运行 | 全量扫描（浪费） | 缓存命中，直接返回 | 更优 |
| 规则版本变更后 | 全量扫描 | `WHERE cache_version` 过滤后为空 → 全量扫描 | 一致 |
| 全量扫描后再次点击 | 再次全量扫描 | 缓存命中 | 更优 |
| 无规则命中文件 | 返回空列表 | upsertedCount=0，缓存为空，下次仍全量扫描 | 一致 |
| AI 分析更新缓存后 | 全量扫描（AI 结果未利用） | 直接读取已更新缓存 | 更优 |

---

## 10. 测试要点

### 10.1 单元测试

| # | 测试场景 | 预期行为 |
|---|---|---|
| 1 | `GetByActionAndVersionAsync` 返回 >0 条 | 直接返回缓存结果，不调用 EnumerateFilesAsync |
| 2 | 缓存命中时返回列表元素均为 `FinalAction == targetAction` | 断言所有元素 Action 匹配 |
| 3 | 缓存为空返回 0 条 | 触发全量扫描 |
| 4 | 版本过期（SQL 返回空） | 触发全量扫描 |
| 5 | 全量扫描时验证 `_cacheRepo.Upsert` 被调用 | Mock 验证 Upsert 调用次数 >0 |
| 6 | 全量扫描时验证 `FlushAsync` 被调用 | Mock 验证 FlushAsync 被调用 1 次 |
| 7 | 只写入 RuleVerdict != Keep 的文件 | KEEP 文件不调用 Upsert |
| 8 | 缓存读取异常时降级 | 抛出异常 → 全量扫描，不抛出到上层 |
| 9 | 两次 QuickClean：第一次全量，第二次命中 | Mock 验证第二次不调用 EnumerateFilesAsync |
| 10 | Upsert 对象中 CacheVersion 等于 config.RuleCacheVersion | 验证 Upsert 参数版本正确 |

### 10.2 集成测试

| # | 测试场景 | 预期行为 |
|---|---|---|
| 11 | 先 SimulateAsync，再 QuickClean | QuickClean 缓存命中，不触发 Everything 扫描 |
| 12 | 空数据库首次 QuickClean | 全量扫描，行为与改造前一致 |
| 13 | 规则变更后 QuickClean | 缓存 miss → 全量扫描 |

### 10.3 性能基准

| # | 测试场景 | 基线 | 目标 |
|---|---|---|---|
| 14 | 缓存命中：10 万条 AutoDelete | 8-15 秒 | <500ms |
| 15 | 缓存未命中：100 万文件全量扫描 | 8-15 秒 | 8-15 秒（不变） |

---

## 11. 实现步骤

### Phase 1: 接口与仓储（~30 分钟）

1. 在 `IFileDecisionCacheRepository.cs` 中插入 `GetByActionAndVersionAsync` 方法签名
2. 在 `FileDecisionCacheRepository.cs` 中插入完整实现（SQL + MapFromReader）

### Phase 2: ViewModel 改造（~60 分钟）

1. 在 `CollectCandidatesAsync` 方法顶部添加缓存优先读取逻辑
2. 在全量扫描循环内添加 `_cacheRepo.Upsert(arbitrated)` + upsertedCount
3. 全量扫描结束后添加 `await _cacheRepo.FlushAsync(ct)` + log

### Phase 3: 测试（~60 分钟）

1. 编写单元测试覆盖表 10.1 的全部 10 个场景
2. 编写集成测试覆盖表 10.2 的 3 个场景
3. 运行 `dotnet test` 验证

### Phase 4: 回归验证（~30 分钟）

1. 手动测试 QuickClean 和 SmartSelect 在模拟运行后的缓存命中
2. 验证首次使用（空数据库）正常
3. 验证规则变更后缓存失效

---

## 12. 不在此方案内

- **缓存预热**: 不在应用启动时自动触发模拟运行来预热缓存
- **扫描范围校验**: 不校验 IncludedVolumes 或 PathFilter 与缓存内容的一致性
- **增量缓存更新**: 不实现 FileSystemWatcher 驱动的增量缓存更新
- **缓存大小监控**: 不监控 FileDecisionCache 表大小或添加 LRU 淘汰机制
