# B7 AI顾问 — 阶段性关键决策与细节控制文档

---

## 一、关键架构/设计决策记录

### 1.1 速率限制方案

**最终决策**: 采用令牌桶算法（Token Bucket）结合 SemaphoreSlim 并发控制。桶容量 30 令牌，补充速率 0.5 令牌/秒，并发上限 5。获取令牌时使用 100ms 自旋等待。

**核心权衡（Trade-off）**: 
- 牺牲了亚毫秒级响应（100ms 自旋轮询而非事件驱动），换取极简实现（无需后台 Timer 线程，无生命周期管理开销）。
- 对桌面应用场景，100ms 的等待粒度完全可接受。

**被否决的替代方案**:
1. 基于 System.Threading.Channels 的限流管道 — 放弃原因：引入额外的生产者-消费者复杂度，对"每文件独立请求"模式过度设计。
2. 纯 SemaphoreSlim 时间窗口限流（1分钟窗口） — 放弃原因：无法处理突发流量；若前 5 秒用完 30 次额度，后 55 秒完全阻塞。

### 1.2 熔断器实现

**最终决策**: 固定阈值熔断器（3 次连续失败→熔断 30 秒→半开探针），无指数退避。

**核心权衡（Trade-off）**:
- 牺牲了生产环境的精细化故障恢复能力（无指数退避、无部分熔断），换取 V1 实现的简洁性和可测试性。
- 30 秒恢复时间短于 AI API 的 prompt cache TTL（5 分钟），确保短暂服务中断后快速恢复。

**被否决的替代方案**:
1. Polly 库（Microsoft.Extensions.Http.Resilience）— 放弃原因：增加外部 NuGet 依赖；桌面应用不需要 Polly 的复杂策略组合（重试+超时+熔断+舱壁）。
2. 基于滑动窗口的熔断（如 60 秒内失败率 > 50%）— 放弃原因：桌面应用的 AI 调用频率远低于服务端，连续失败计数更直观，且不需要区分"频率"与"总量"。

### 1.3 每日调用计数持久化

**最终决策**: 独立 JSON 文件（`ai_usage.json`），原子写入（临时文件 + Replace），日期变更时自动重置。

**核心权衡（Trade-off）**:
- 牺牲了跨进程实时同步（无 IPC 通知机制），换取模块独立性和最小化对 UserConfig 的侵入。
- 多实例同时运行时可能各自超出日限（各自独立计数），但对桌面应用此场景极少发生。

**被否决的替代方案**:
1. 在 UserConfig 中添加 `AIDailyUsedCount` + `AIDailyUsedDate` 字段 — 放弃原因：污染核心配置结构；日限追踪是 AI 模块的内部状态，不应暴露到全局配置。
2. SQLite 表持久化 — 放弃原因：对单个整数的日限追踪来说过于重量级；引入对 Data 层的写依赖。

### 1.4 HTTP 端点构建策略

**最终决策**: 三种服务类型（openai/ollama/custom）通过 `BuildApiUrl()` 方法统一构建端点。Ollama 默认 `http://localhost:11434`，自动追加 `/v1/chat/completions`。

**核心权衡（Trade-off）**:
- Ollama 的 OpenAI 兼容端点（`/v1/chat/completions`）从 0.1.32 版本开始支持。对于更早版本的用户，此端点不可用。
- 决策：不兼容 Ollama < 0.1.32（约 2024 年 3 月前版本），文档中应注明最低版本要求。

**被否决的替代方案**:
1. 同时支持 Ollama 原生 API（`/api/generate`）和 OpenAI 兼容端点 — 放弃原因：需维护两套请求/响应解析逻辑，增加约 40% 代码量，且 Ollama 原生 API 不支持 system prompt。

---

## 二、隐含前提与关键假设清单（风险预警）

1. **[假设1]** AI 模型严格遵循提示词中的 JSON-only 输出指令。若模型经常输出额外文本（如 "好的，分析如下：{...}"），防御性解析（正则 + markdown 剥离）能处理 80% 的情况，但仍有解析失败的可能。
2. **[假设2]** 用户的 AI API 端点在 30 秒内响应单文件分析请求。对大模型（如 70B+ 参数）或高负载的自定义 API，30 秒可能不足。若超时频繁，需将 `ApiTimeout` 从 30 秒调整为 60 秒。
3. **[假设3]** `IFileDecisionCacheRepository.Upsert()` 是同步非阻塞方法（入队即返回）。若后续实现改为异步方法，`ApplyResultToCache()` 需要相应调整。
4. **[假设4]** 桌面应用为单实例运行。多实例场景下 `ai_usage.json` 的日限计数可能不准确。
5. **[假设5]** Ollama 版本 ≥ 0.1.32，支持 `/v1/chat/completions` 端点。若用户使用更早版本，Ollama 模式不可用。
6. **[假设6]** `IConfigService.Current` 返回的 `UserConfig` 引用在配置变更时被替换为新实例（不可变语义），而非原地修改。AI 服务每批次开始时捕获快照，批次中途的配置变更不影响当前批次。

---

## 三、边界与范围界定（明确"不做什么"）

1. **[明确不做]** 本期不做 AI 分析进度条（Progress Bar）。调用方可通过 `IProgress<int>` 扩展点自行实现。
2. **[明确不做]** 本期不做 Prompt 模板的可配置化。系统提示词硬编码在 `AiPromptBuilder.BuildSystemPrompt()` 中。若需支持用户自定义提示词，在后续版本中通过 JSON 配置文件加载。
3. **[明确不做]** 本期不做多文件合并请求（即一次 API 调用分析多个文件）。设计文档定义每文件独立调用，批处理留给后续版本。
4. **[明确不做]** 本期不做 AI 分析结果的历史记录查询。分析结果仅存储在 `FileDecisionCache` 中，随缓存失效而清空。
5. **[明确不做]** 本期不做 AI 服务端流式响应（SSE / Server-Sent Events）。所有调用使用标准的请求-响应模式。
6. **[明确不做]** 本期不做 AI 服务类型的自动检测。用户需手动选择 OpenAI / Ollama / Custom。
7. **[明确不做]** 本期不做 AI 调用费用的计算与展示。API 费用追踪留给后续版本。

---

## 四、待确认事项（需要人工介入的模糊点）

1. **[待确认项A]** `AiPromptBuilder.BuildSystemPrompt()` 中的中文提示词内容。当前提示词为通用磁盘清理顾问角色定义。若需要针对特定用户群体（如开发者/设计师）定制提示词，需业务方提供具体的话术。
2. **[待确认项B]** 熔断器恢复时间（30 秒）和失败阈值（3 次）。当前值基于通用经验设定，若实际使用中发现过于敏感或迟钝，需调整这两个参数。
3. **[待确认项C]** 自动触发的文件大小阈值（100 MB）。当前值来自设计文档 V3.7 第 3.5 节。若实际使用中发现此阈值过滤效果不佳（候选文件过多或过少），需调整。
4. **[待确认项D]** AI 标签的三个合法值 `safe/unknown/risky` 与 Prompt 中的中文映射。当前 Prompt 使用英文标签值（便于 JSON 解析），若 AI 模型偶尔输出中文标签（"安全"/"未知"/"风险"），`ValidateLabel()` 会将其校正为 `unknown`。需确认是否需要支持中文标签值。
5. **[待确认项E]** `AIDailyLimit` 默认值 50。此值来自 UserConfig 默认值，对应设计文档 3.7 节。若面向专业用户，可能需要更大的日限（如 200）。

---

## 五、关键外部依赖锁定

| 依赖项 | 提供方 | 状态 | 若延期的影响 |
|--------|--------|------|-------------|
| `IConfigService` | A4 配置管理层 | ✅ 已完成 | AI 顾问无法读取配置 |
| `IFileDecisionCacheRepository` | A3 数据层 | ✅ 已完成 | AI 分析结果无法持久化 |
| `IDecisionEngine` | B3 决策引擎 | ✅ 已完成 | AI 标签无法参与决策仲裁 |
| OpenAI API / Ollama / 自定义 API | 外部服务 | ⚠️ 取决于用户部署 | 若无可用 AI 端点，AI 顾问全部功能不可用 |
| Everything SDK | A2 Everything 适配器 | ✅ 已完成 | 间接影响——无 Everything 则无文件扫描，无文件则无 AI 分析 |
| HttpClient (.NET 8) | .NET 运行时 | ✅ 内置于运行时 | 无额外依赖 |

### 关键风险

- **OpenAI API 兼容性**: 若 OpenAI 修改 Chat Completions API 的请求/响应格式，需要更新 `AiApiRequest` 和 `AiApiResponse` 的 DTO 模型。
- **Ollama 版本**: Ollama < 0.1.32 不支持 `/v1/chat/completions` 端点，Ollama 用户需升级。
- **自定义 API 兼容性**: 自定义 API 必须实现 OpenAI-compatible 的 `/v1/chat/completions` 端点。

---

## 六、文件清单

### 新增文件（10 个）

| 文件 | 行数（估算） | 说明 |
|------|-------------|------|
| `src/DirectoryCleanAgent.AI/Models/AiApiRequest.cs` | ~35 | API 请求体 DTO |
| `src/DirectoryCleanAgent.AI/Models/AiApiResponse.cs` | ~95 | API 响应体 + AiAnalysisResult |
| `src/DirectoryCleanAgent.AI/IAiAdvisorService.cs` | ~85 | 公开服务接口 |
| `src/DirectoryCleanAgent.AI/AiAdvisorService.cs` | ~370 | 主协调器实现 |
| `src/DirectoryCleanAgent.AI/AiRateLimiter.cs` | ~110 | 令牌桶限流器 |
| `src/DirectoryCleanAgent.AI/AiCircuitBreaker.cs` | ~180 | 熔断器状态机 |
| `src/DirectoryCleanAgent.AI/AiDailyUsageTracker.cs` | ~195 | 每日调用追踪 |
| `src/DirectoryCleanAgent.AI/AiPromptBuilder.cs` | ~210 | 提示词构建与解析 |
| `src/DirectoryCleanAgent.AI/AiServiceRegistration.cs` | ~105 | DI 注册扩展 |
| `tests/B7-Postman测试用例.json` | ~350 | Postman 测试集合 |

### 修改文件（4 个）

| 文件 | 修改内容 |
|------|---------|
| `src/DirectoryCleanAgent.AI/DirectoryCleanAgent.AI.csproj` | 添加 Data 项目引用 |
| `src/DirectoryCleanAgent.AI/Placeholder.cs` | 替换为模块说明注释 |
| `src/DirectoryCleanAgent/DirectoryCleanAgent.csproj` | 添加 AI 项目引用 |
| `src/DirectoryCleanAgent/App.xaml.cs` | 添加 `using` 和 `services.AddAiLayer()` |

### 总代码量

约 1,500 行 C# 代码 + 350 行 Postman JSON。
