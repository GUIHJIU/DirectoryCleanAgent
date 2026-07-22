# 审查报告："一键清理安全项 — 缓存复用方案设计"

**日期**: 2026-07-18
**总体评估**: ✅ 有条件通过

---

## 必须修复的关键问题

### 严重 #1：缓存读取前未刷新 BatchWriteQueue，AI 结果不可见

`AiAdvisorService.ApplyResultToCache` 通过 `_cacheRepo.Upsert()` 写回 AI 分析结果，数据进入 `BatchWriteQueue`（每 500ms/200条 刷新）。如果在 AI 分析刚完成、队列尚未刷新时调用 QuickClean，`GetByActionAndVersionAsync` 会读到过时数据。

**修复**: 在缓存读取前添加 `await _cacheRepo.FlushAsync(ct)`。

### 严重 #2：日志格式不一致

设计文档使用 `Logger.Info(...)`，项目实际使用 `_logger.LogInformation(...)` 结构化日志。

**修复**: 统一使用 Microsoft.Extensions.Logging 结构化日志格式。

---

## 建议修复的中等问题

### #3：无法区分"此版本无匹配项"与"版本过期"

两者都返回空列表，日志无法区分，不利于调试。

### #4：缓存命中路径缺少进度报告

用户会看到"正在检查容量..."停顿后直接跳到"正在哈希处理"。

### #5：config 快照一致性

应在方法开始时捕获 `_configService.Current` 快照，全流程使用同一快照。

### #6：测试场景 #9 需要带状态的 mock

第二次调用需验证不触发 Everything 扫描，需要内存 SQLite 或有状态 mock。

---

## 轻微问题

### #7：缓存命中结果无序
### #8：AI 并发测试场景缺失
### #9：启动时缓存自动失效路径未文档化
