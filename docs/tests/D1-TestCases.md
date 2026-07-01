# D1 单元测试用例文档

## 概述

- **框架**: xUnit 2.9.0 + Moq 4.20.70 + Microsoft.Data.Sqlite 8.0.11
- **覆盖率目标**: >80% 行覆盖率（B1-B7 全部模块）
- **测试项目**: `src/DirectoryCleanAgent.Tests/`
- **对应任务**: D1 - 单元测试（核心逻辑）

## B7 AI 模块测试用例（新增 100+ 个测试）

### 1. AiCircuitBreaker（17 个用例）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| CB-01 | Constructor_NullLogger | - | new AiCircuitBreaker(null) | ArgumentNullException |
| CB-02 | Constructor_ValidLogger | - | new AiCircuitBreaker(logger) | 实例创建成功 |
| CB-03 | State_Initial_ReturnsClosed | 新实例 | 读取 State | Closed |
| CB-04 | EnsureNotOpen_WhenClosed | State=Closed | EnsureNotOpen() | 无异常 |
| CB-05 | EnsureNotOpen_WhenHalfOpen | State=HalfOpen | EnsureNotOpen() | 无异常（探针放行） |
| CB-06 | EnsureNotOpen_OpenBeforeTimeout | State=Open, 超时未到 | EnsureNotOpen() | CircuitBreakerOpenException |
| CB-07 | EnsureNotOpen_OpenAfterTimeout | State=Open, 超时已过 | EnsureNotOpen() | 自动转 HalfOpen |
| CB-08 | RecordSuccess_WhenClosed | 有 2 次失败 | RecordSuccess() | 计数重置，保持 Closed |
| CB-09 | RecordSuccess_WhenHalfOpen | State=HalfOpen | RecordSuccess() | HalfOpen→Closed |
| CB-10 | RecordSuccess_WhenOpen | State=Open | RecordSuccess() | 防御：Open→Closed |
| CB-11 | RecordFailure_BelowThreshold | 1-2 次失败 | RecordFailure() | 保持 Closed |
| CB-12 | RecordFailure_ThirdFailure | 2 次失败 | RecordFailure() | Closed→Open |
| CB-13 | RecordFailure_WhenHalfOpen | State=HalfOpen | RecordFailure() | HalfOpen→Open（重新熔断） |
| CB-14 | RecordFailure_WhenOpen | State=Open | RecordFailure() | 保持 Open |
| CB-15 | State_ThreadSafety | 多线程并发 | 并发读写状态 | 无死锁 |
| CB-16 | Exception_WithMessage | - | new CircuitBreakerOpenException(msg) | 消息正确 |
| CB-17 | Exception_WithInner | - | new CircuitBreakerOpenException(msg, inner) | 内部异常正确 |

### 2. AiRateLimiter（15 个用例，3 个跳过）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| RL-01 | Constructor_NullLogger | - | new AiRateLimiter(null) | ArgumentNullException |
| RL-02 | Constructor_ValidLogger | - | new AiRateLimiter(logger) | 实例创建成功 |
| RL-03 | FirstAcquire_ReturnsTrue | 新实例 | TryAcquireAsync() | true |
| RL-04 | MultipleAcquires_UpToMax | 令牌桶满(30) | 30 次 TryAcquireAsync | 全部成功 |
| RL-05 | ExhaustsTokens_Replenishes | 令牌=0 | TryAcquireAsync + 等待 | Skipped（耗时） |
| RL-06 | ReplenishNotExceedMax | 令牌=30 | 等待后获取 | 令牌≤30 |
| RL-07 | ConcurrentUpTo5_AllSucceed | 新实例 | 5 并发 TryAcquireAsync | 全部成功 |
| RL-08 | ConcurrentExceedsMax_Blocks | 5 槽已占 | 第 6 个 TryAcquireAsync | Skipped（耗时） |
| RL-09 | CancelledDuringWait | 信号量满 | TryAcquireAsync+取消 | Skipped（耗时） |
| RL-10 | Release_AfterAcquire | 已获取 | Release() | 不抛异常 |
| RL-11 | Release_WhenNoAcquisitions | 未获取 | Release() | 不抛异常（防御） |
| RL-12 | Release_MultipleExtra | 未获取 | 10 次 Release() | 不抛异常 |
| RL-13 | Dispose_Idempotent | - | Dispose(); Dispose() | 不抛异常 |
| RL-14 | Dispose_MultipleCalls_Safe | - | 3 次 Dispose() | 不抛异常 |
| RL-15 | Cancelled_ReleasesSlot | 令牌=0 | 50ms 后取消 | Skipped（耗时） |

### 3. AiDailyUsageTracker（19 个用例）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| UT-01 | Constructor_NullLogger | - | new Tracker(path, 50, null) | ArgumentNullException |
| UT-02 | Constructor_CreatesDir | 目录不存在 | new Tracker() | 目录已创建 |
| UT-03 | DefaultPath_WhenEmpty | dataPath="" | new Tracker() | 使用 LocalAppData |
| UT-04 | Load_NoFile_InitZero | 无文件 | LoadAsync() | UsedCount=0 |
| UT-05 | Load_ValidFile | 文件存有 count=15 | LoadAsync() | UsedCount=15 |
| UT-06 | Load_DateChanged | 文件存昨日数据 | LoadAsync() | UsedCount=0（重置） |
| UT-07 | Load_CorruptedJson | 文件存乱码 | LoadAsync() | UsedCount=0 + 备份 |
| UT-08 | Load_EmptyJson | 文件存 null | LoadAsync() | UsedCount=0 |
| UT-09 | Load_CountExceedsLimit | 文件存 count=999, 日限=50 | LoadAsync() | UsedCount=50（钳位） |
| UT-10 | Increment_IncrementsAndPersists | UsedCount=0 | IncrementAsync() | count=1, 文件已写入 |
| UT-11 | Increment_DateChange | 昨天日期 | IncrementAsync() | 重置后 count=1 |
| UT-12 | Increment_AtomicWrite | UsedCount=0 | IncrementAsync() | .tmp 不残留 |
| UT-13 | Increment_MultipleIncrements | UsedCount=0 | 10 次 IncrementAsync | UsedCount=10 |
| UT-14 | Increment_ExceedsLimit | 日限=5 | 10 次 IncrementAsync | UsedCount=10, IsLimitReached=true |
| UT-15 | IsLimitReached_AtLimit | 日限=3, count=3 | 检查 IsLimitReached | true |
| UT-16 | RemainingCount | 日限=50, count=2 | 检查 RemainingCount | 48 |
| UT-17 | WriteFailure_ReturnsMemCount | 目录已删除 | IncrementAsync() | 内存计数=2 |
| UT-18 | ThreadSafety | 20 并发 | 并发 IncrementAsync | UsedCount=20 |
| UT-19 | Dispose_Idempotent | - | Dispose() x2 | 不抛异常 |

### 4. AiPromptBuilder（37 个用例）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| PB-01~03 | BuildSystemPrompt 内容完整性 | - | BuildSystemPrompt() | 含 label/confidence/explanation |
| PB-04~08 | BuildUserPrompt 格式化 | 各种文件属性 | BuildUserPrompt() | 正确 MB、天数、路径 |
| PB-09 | BuildTestPrompt | - | BuildTestPrompt() | 含 ok 状态 |
| PB-10~23 | ParseResponse 三层策略 | 各种响应格式 | ParseResponse() | 正确解析/null |
| PB-24~37 | ValidateLabel 标签校验 | 各种标签输入 | ValidateLabel() | 合法则返回/非法则 unknown |

### 5. AiAdvisorService（24 个用例）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| AS-01~04 | Constructor null 守卫 | - | new Svc(null, ...) | ArgumentNullException |
| AS-05 | IsAvailable_AllTrue | 全部条件满足 | IsAvailable | true |
| AS-06~09 | IsAvailable_Disabled | 各禁用条件 | IsAvailable | false |
| AS-10 | AnalyzeFile_NullPath | filePath=null | AnalyzeFileAsync | ArgumentNullException |
| AS-11 | AnalyzeFile_NotFound | 文件不存在 | AnalyzeFileAsync | null |
| AS-12 | AnalyzeFile_Unavailable | AIEnabled=false | AnalyzeFileAsync | null |
| AS-13~14 | AnalyzeFiles 空/null 输入 | 空/null | AnalyzeFilesAsync | 空/ArgumentNullException |
| AS-15 | AnalyzeFiles_Truncate200 | 250 文件 | AnalyzeFilesAsync | 截断到 200 |
| AS-16 | AnalyzeFiles_SkipAnalyzed | 含 AiLabel 文件 | AnalyzeFilesAsync | 跳过已分析 |
| AS-17~20 | AutoAnalyzeUncategorized 筛选 | 混合文件 | AutoAnalyzeUncategorizedAsync | 正确筛选 |
| AS-21 | DailyUsedCount | - | DailyUsedCount | 初始=0 |
| AS-22 | Dispose_Idempotent | - | Dispose() x2 | 不抛异常 |

### 6. AiAdvisorServiceHttpTests（18 个用例）

| 用例ID | 名称 | 前置条件 | 操作 | 预期结果 |
|--------|------|---------|------|---------|
| HT-01 | HTTP200_Success | Mock 200 | AnalyzeFileAsync | Label=safe |
| HT-02 | HTTP401_Failure | Mock 401 | AnalyzeFilesAsync | IsSuccess=false |
| HT-03 | HTTP403_Failure | Mock 403 | AnalyzeFilesAsync | IsSuccess=false |
| HT-04 | HTTP429_RecordsFailure | Mock 429 | AnalyzeFilesAsync | IsSuccess=false |
| HT-05 | HTTP500_Failure | Mock 500 | AnalyzeFilesAsync | IsSuccess=false |
| HT-06 | HTTP503_Failure | Mock 503 | AnalyzeFilesAsync | IsSuccess=false |
| HT-07 | Timeout_Failure | Mock Timeout | AnalyzeFilesAsync | IsSuccess=false |
| HT-08 | AuthHeader_Present | Mock 200 | AnalyzeFileAsync | Bearer 头存在 |
| HT-09 | ContentType_Json | Mock 200 | AnalyzeFileAsync | application/json |
| HT-10 | TestConnection_Success | Mock 200 | TestConnectionAsync | true |
| HT-11 | TestConnection_NonSuccess | Mock 400 | TestConnectionAsync | false |
| HT-12 | TestConnection_Timeout | Mock Timeout | TestConnectionAsync | false |
| HT-13~18 | URL 构建 | 各 serviceType | BuildApiUrl (反射) | 正确 URL |

### 7. AiModelSerialization（13 个用例）

| 用例ID | 名称 | 操作 | 预期结果 |
|--------|------|------|---------|
| MS-01~03 | 请求序列化/反序列化/默认值 | Serialize/Deserialize | JSON 契约正确 |
| MS-04~08 | ExtractContent 提取逻辑 | ExtractContent() | 正确提取/null |
| MS-09~11 | AiAnalysisResult 工厂方法 | Success/Failure() | IsSuccess/字段正确 |
| MS-12~13 | 默认值验证 | new AiModelResponse() | 默认值正确 |

## 现有模块增强（10+ 用例）

### B4 OperationExecutor 增强
- OE-新增: 跨卷 .cleaning.tmp 创建与清理
- OE-新增: 文件锁 (0x80070020) → manual_review 降级
- OE-新增: 删除前文件不存在 → 优雅跳过

### B2 RuleEngine 增强
- RE-新增: 热重载 RuleCacheVersion 递增
- RE-新增: 500ms 防抖验证
- RE-新增: 规则变更事件传播链

### B5 BackupManager 增强
- BM-新增: 跨卷回滚路径处理
- BM-新增: 路径冲突 _restored 后缀
- BM-新增: 恢复后墓碑清除验证

## 运行方式

```bash
# 全部 B7 测试（跳过耗时的自旋测试）
dotnet test src/DirectoryCleanAgent.Tests --filter "FullyQualifiedName~AI"

# 覆盖率测量
dotnet test src/DirectoryCleanAgent.Tests \
  --settings src/DirectoryCleanAgent.Tests/coverlet.runsettings \
  /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura \
  /p:CoverletOutput=./TestResults/Coverage/
```
