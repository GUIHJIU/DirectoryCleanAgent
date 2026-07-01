// AI 顾问模块已在 B7 阶段完整实现。
// 各个组件的代码文件：
// - IAiAdvisorService.cs      — 公开服务接口
// - AiAdvisorService.cs        — 主协调器实现（管道：限流→熔断→调API→解析→缓存更新→重新仲裁）
// - AiRateLimiter.cs           — 令牌桶限流器（30 RPM + 并发5）
// - AiCircuitBreaker.cs        — 熔断器状态机（3次失败→熔断30秒→半开探针→恢复）
// - AiDailyUsageTracker.cs     — 每日调用次数持久化追踪
// - AiPromptBuilder.cs         — 提示词构建 + 防御性JSON解析 + 标签验证
// - AiServiceRegistration.cs   — DI 容器注册扩展方法
// - Models/AiApiRequest.cs     — Chat Completions API 请求体
// - Models/AiApiResponse.cs    — API 响应体 + AiAnalysisResult

namespace DirectoryCleanAgent.AI;
