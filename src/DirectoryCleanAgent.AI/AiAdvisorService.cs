using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 顾问服务主实现。协调限流、熔断、提示词构建、API 调用、缓存更新和重新仲裁。
///
/// 每个文件的完整处理管道：
///   1. EnsureNotOpen()     → 检查熔断器状态
///   2. TryAcquireAsync()   → 获取令牌和并发槽位
///   3. CheckDailyLimit()   → 检查日限
///   4. BuildPrompts()      → 构建系统+用户提示词
///   5. CallAiApiAsync()    → 发送 HTTP POST 到 AI API
///   6. ParseResponse()     → 解析 JSON 响应
///   7. ValidateLabel()     → 验证标签合法性
///   8. ApplyResultToCache()→ 更新缓存 + 重新仲裁 + 持久化
///   9. finally Release()   → 释放并发槽位
///
/// 错误处理原则：
///   - 单个文件失败不中断批次（跳过该文件继续处理下一个）
///   - 熔断器打开时立即停止所有请求（快速失败）
///   - 401/403 认证错误视为致命错误，停止批次
///   超时不阻塞主流程（设计文档 3.5 节要求）
/// </summary>
public sealed class AiAdvisorService : IAiAdvisorService, IDisposable
{
    private const int ManualBatchMaxFiles = 200;      // 手动触发单次最多 200 文件
    private const int AutoBatchMaxFiles = 500;         // 自动触发单次最多 500 文件
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30); // 单次 API 调用超时

    private readonly IConfigService _configService;
    private readonly IFileDecisionCacheRepository _cacheRepo;
    private readonly IDecisionEngine _decisionEngine;
    private readonly ILogger<AiAdvisorService> _logger;
    private readonly AiRateLimiter _rateLimiter;
    private readonly AiCircuitBreaker _circuitBreaker;
    private readonly AiDailyUsageTracker _usageTracker;
    private readonly AiPromptBuilder _promptBuilder;
    private readonly HttpClient _httpClient;

    private bool _disposed;

    internal AiAdvisorService(
        IConfigService configService,
        IFileDecisionCacheRepository cacheRepo,
        IDecisionEngine decisionEngine,
        ILogger<AiAdvisorService> logger,
        AiRateLimiter rateLimiter,
        AiCircuitBreaker circuitBreaker,
        AiDailyUsageTracker usageTracker,
        AiPromptBuilder promptBuilder,
        HttpClient httpClient)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _cacheRepo = cacheRepo ?? throw new ArgumentNullException(nameof(cacheRepo));
        _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // 初始化时加载历史用量
        _ = LoadUsageAsync();

        _logger.LogInformation("AI 顾问服务初始化完成（日限={Limit}次/天）", _configService.Current.AIDailyLimit);
    }

    // ============================================================
    // 公开属性
    // ============================================================

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            var config = _configService.Current;
            if (!config.AIEnabled) return false;
            if (string.IsNullOrWhiteSpace(config.AIApiUrl)) return false;
            if (_usageTracker.IsLimitReached) return false;
            if (_circuitBreaker.State == AiCircuitBreaker.BreakerState.Open) return false;
            return true;
        }
    }

    /// <inheritdoc />
    public int DailyUsedCount => _usageTracker.UsedCount;

    /// <inheritdoc />
    public int DailyRemainingCount => _usageTracker.RemainingCount;

    // ============================================================
    // 单文件分析（UI 点击单个文件的"AI分析"按钮）
    // ============================================================

    /// <inheritdoc />
    public async Task<AiAnalysisResult?> AnalyzeFileAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"文件={filePath}");
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            if (!IsAvailable)
            {
                _logger.LogWarning("AI 服务不可用，分析被跳过: {Path}", filePath);
                return null;
            }

            // 步骤1：检查熔断器（Open→HalfOpen 转换在此发生）
            _circuitBreaker.EnsureNotOpen();

            // 步骤2：获取速率令牌和并发槽位
            var acquired = await _rateLimiter.TryAcquireAsync(ct);
            if (!acquired)
                return null;

            try
            {
                // 步骤3：检查日限
                if (_usageTracker.IsLimitReached)
                {
                    _logger.LogWarning("AI 日限已达 ({Used}/{Limit})，跳过单文件分析: {Path}",
                        _usageTracker.UsedCount, _configService.Current.AIDailyLimit, filePath);
                    return AiAnalysisResult.Failure(filePath, "每日 AI 调用次数已达上限");
                }

                // 从操作系统获取文件元数据（构建提示词需要）
                FileInfo? fileInfo = null;
                try
                {
                    fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        _logger.LogWarning("文件不存在，无法分析: {Path}", filePath);
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取文件信息失败: {Path}", filePath);
                    return null;
                }

                // 尝试获取已有缓存（若存在则更新，不存在则仅返回分析结果）
                var cache = await _cacheRepo.GetByFilePathAsync(filePath, ct);

                // 构建提示词
                var userPrompt = _promptBuilder.BuildUserPrompt(filePath, fileInfo.Length, fileInfo.LastWriteTimeUtc);

                // 步骤4-5：执行 AI 调用并解析响应
                var aiResponse = await CallAiApiWithPipelineAsync(userPrompt, ct);

                if (aiResponse == null)
                {
                    return AiAnalysisResult.Failure(filePath, "AI API 调用失败或响应解析失败");
                }

                // 步骤6：验证标签
                var label = _promptBuilder.ValidateLabel(aiResponse.Label);

                // 若存在缓存则更新
                if (cache != null)
                {
                    var config = _configService.Current;
                    ApplyResultToCache(cache, label, aiResponse.Confidence, aiResponse.Explanation,
                        config.AIEnabled, config.AITrustLevel);
                }

                // 步骤7：递增日限计数
                await _usageTracker.IncrementAsync();

                var result = AiAnalysisResult.Success(filePath, label, aiResponse.Confidence, aiResponse.Explanation);
                _logger.LogInformation("AI 单文件分析成功: {Path} → {Label} (置信度={Confidence:P})",
                    filePath, label, aiResponse.Confidence);

                return result;
            }
            finally
            {
                // 释放并发槽位
                _rateLimiter.Release();
            }
        }
        catch (CircuitBreakerOpenException ex)
        {
            _logger.LogWarning("熔断器打开，跳过单文件分析: {Path} —— {Message}", filePath, ex.Message);
            return AiAnalysisResult.Failure(filePath, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AI 单文件分析被取消: {Path}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 单文件分析异常: {Path}", filePath);
            return AiAnalysisResult.Failure(filePath, ex.Message);
        }
    }

    // ============================================================
    // 批量文件分析（UI 用户多选后点击"AI分析"）
    // ============================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeFilesAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"文件数={files?.Count ?? 0}");
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        if (!IsAvailable)
        {
            _logger.LogWarning("AI 服务不可用，批量分析被跳过（{Count} 个文件）", files.Count);
            return Array.Empty<AiAnalysisResult>();
        }

        // 截断到手动批次上限
        var batch = files.Count > ManualBatchMaxFiles
            ? files.Take(ManualBatchMaxFiles).ToList()
            : files.ToList();

        if (files.Count > ManualBatchMaxFiles)
        {
            _logger.LogWarning("手动触发文件数 {Total} 超过上限 {Max}，已截断为 {Count}",
                files.Count, ManualBatchMaxFiles, batch.Count);
        }

        // 过滤已分析文件（已有 ai_label 的跳过）
        var toAnalyze = batch.Where(f => string.IsNullOrEmpty(f.AiLabel)).ToList();
        var skippedCount = batch.Count - toAnalyze.Count;

        if (skippedCount > 0)
        {
            _logger.LogInformation("跳过 {Count} 个已分析的文件", skippedCount);
        }

        if (toAnalyze.Count == 0)
        {
            _logger.LogInformation("没有需要分析的文件（全部已有 AI 标签）");
            return Array.Empty<AiAnalysisResult>();
        }

        // 捕获配置快照（批次内一致性，避免配置中途变更导致不一致）
        var configSnapshot = _configService.Current;
        var aiEnabled = configSnapshot.AIEnabled;
        var trustLevel = configSnapshot.AITrustLevel;

        // 并发处理所有文件（由速率限制器控制并发度和速率）
        var tasks = toAnalyze.Select(cache => ProcessSingleFileAsync(cache, aiEnabled, trustLevel, ct));
        var results = await Task.WhenAll(tasks);

        var validResults = results.Where(r => r != null).Cast<AiAnalysisResult>().ToList();

        _logger.LogInformation("批量 AI 分析完成: 成功={Success}, 失败={Failed}, 跳过={Skipped}",
            validResults.Count(r => r.IsSuccess),
            validResults.Count(r => !r.IsSuccess),
            skippedCount);

        return validResults.AsReadOnly();
    }

    // ============================================================
    // 自动分析未分类大文件（专家模式扫描完成后自动触发）
    // ============================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiAnalysisResult>> AutoAnalyzeUncategorizedAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"全量文件数={allFiles?.Count ?? 0}");
        ArgumentNullException.ThrowIfNull(allFiles);

        if (!IsAvailable)
        {
            _logger.LogInformation("AI 服务不可用，跳过自动分析");
            return Array.Empty<AiAnalysisResult>();
        }

        // 筛选未分类大文件（设计文档 3.5 节筛选规则）
        var candidates = allFiles
            .Where(f => string.IsNullOrEmpty(f.SemanticCategory)
                     || f.SemanticCategory.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.SizeBytes > 100L * 1024 * 1024)       // > 100 MB
            .Where(f => string.IsNullOrEmpty(f.AiLabel))         // 跳过已分析
            .OrderByDescending(f => f.SizeBytes)                 // 按大小降序
            .Take(AutoBatchMaxFiles)                             // 最多 500
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("自动分析: 没有符合条件的未分类大文件（>100MB 且未分析）");
            return Array.Empty<AiAnalysisResult>();
        }

        _logger.LogInformation("自动分析: 筛选出 {Count} 个未分类大文件（>100MB），开始批量分析", candidates.Count);

        // 批量分析（复用 AnalyzeFilesAsync 的管道，但不经手动截断）
        return await AnalyzeFilesInternalAsync(candidates, ct);
    }

    // ============================================================
    // 连接测试（设置向导 Step4 使用）
    // ============================================================

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        string serviceType, string apiUrl, string apiKey, string model, CancellationToken ct = default)
    {
        _logger.LogMethodEntry($"Type={serviceType}, URL={apiUrl}, Model={model}");

        try
        {
            var (systemPrompt, userPrompt) = _promptBuilder.BuildTestPrompt();
            var url = BuildApiUrl(serviceType, apiUrl);

            using var request = BuildHttpRequest(url, apiKey, model, systemPrompt, userPrompt);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // 连接测试超时 15 秒

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("AI 连接测试成功: {Url} (模型={Model})", url, model);
                return true;
            }

            _logger.LogWarning("AI 连接测试失败: HTTP {StatusCode}, Body={Body}",
                (int)response.StatusCode, body);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("AI 连接测试超时: {Url}", apiUrl);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 连接测试异常: {Url}", apiUrl);
            return false;
        }
    }

    // ============================================================
    // 内部核心方法
    // ============================================================

    /// <summary>
    /// 处理单个文件的完整 AI 分析管道（不加限流，由调用方控制并发）。
    /// 在限流、调用 API、更新缓存的全流程中捕获异常，单个失败不抛出。
    /// </summary>
    private async Task<AiAnalysisResult?> ProcessSingleFileAsync(
        FileDecisionCache cache, bool aiEnabled, AITrustLevel trustLevel, CancellationToken ct)
    {
        try
        {
            // 步骤1：检查熔断器
            _circuitBreaker.EnsureNotOpen();

            // 步骤2：获取速率令牌和并发槽位
            var acquired = await _rateLimiter.TryAcquireAsync(ct);
            if (!acquired)
                return null;

            try
            {
                // 步骤3：检查日限（日限达到是应用层配额限制，不计入熔断器失败计数）
                if (_usageTracker.IsLimitReached)
                {
                    _logger.LogWarning("AI 日限已达 ({Used}/{Limit})，跳过: {Path}",
                        _usageTracker.UsedCount, _configService.Current.AIDailyLimit, cache.FilePath);
                    return AiAnalysisResult.Failure(cache.FilePath, "每日 AI 调用次数已达上限");
                }

                // 步骤4：构建提示词
                var userPrompt = _promptBuilder.BuildUserPrompt(
                    cache.FilePath, cache.SizeBytes, cache.LastWriteTime);

                // 步骤5-6：调用 API 并解析响应
                var aiResponse = await CallAiApiWithPipelineAsync(userPrompt, ct);

                if (aiResponse == null)
                {
                    _circuitBreaker.RecordFailure();
                    return AiAnalysisResult.Failure(cache.FilePath, "AI API 调用失败或响应解析失败");
                }

                // 步骤7：验证标签
                var label = _promptBuilder.ValidateLabel(aiResponse.Label);

                // 步骤8：更新缓存、重新仲裁、持久化
                ApplyResultToCache(cache, label, aiResponse.Confidence, aiResponse.Explanation,
                    aiEnabled, trustLevel);

                // 步骤9：递增日限计数
                await _usageTracker.IncrementAsync();

                // 记录成功
                _circuitBreaker.RecordSuccess();

                return AiAnalysisResult.Success(cache.FilePath, label, aiResponse.Confidence, aiResponse.Explanation);
            }
            finally
            {
                // 释放并发槽位（必须与 TryAcquireAsync 成对调用）
                _rateLimiter.Release();
            }
        }
        catch (CircuitBreakerOpenException ex)
        {
            _logger.LogWarning("熔断器打开，停止分析: {Path} —— {Message}", cache.FilePath, ex.Message);
            return AiAnalysisResult.Failure(cache.FilePath, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("AI 分析被取消: {Path}", cache.FilePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 分析异常: {Path}", cache.FilePath);
            _circuitBreaker.RecordFailure();
            return AiAnalysisResult.Failure(cache.FilePath, ex.Message);
        }
    }

    /// <summary>
    /// 内部批量分析方法（不经手动触发上限截断，供自动分析调用）。
    /// </summary>
    private async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeFilesInternalAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct)
    {
        if (files.Count == 0)
            return Array.Empty<AiAnalysisResult>();

        if (!IsAvailable)
        {
            _logger.LogWarning("AI 服务不可用，内部批量分析被跳过");
            return Array.Empty<AiAnalysisResult>();
        }

        var configSnapshot = _configService.Current;
        var aiEnabled = configSnapshot.AIEnabled;
        var trustLevel = configSnapshot.AITrustLevel;

        var tasks = files.Select(cache => ProcessSingleFileAsync(cache, aiEnabled, trustLevel, ct));
        var results = await Task.WhenAll(tasks);

        return results.Where(r => r != null).Cast<AiAnalysisResult>().ToList().AsReadOnly();
    }

    /// <summary>
    /// 执行 AI API 调用的完整流程：发送请求 → 读取响应 → 解析 JSON。
    /// 返回解析后的 AiModelResponse，失败时返回 null。
    /// </summary>
    private async Task<AiModelResponse?> CallAiApiWithPipelineAsync(string userPrompt, CancellationToken ct)
    {
        var config = _configService.Current;
        var systemPrompt = _promptBuilder.BuildSystemPrompt();
        var url = BuildApiUrl(config.AIServiceType, config.AIApiUrl);

        try
        {
            // 发送 HTTP 请求
            var responseContent = await CallAiApiAsync(
                url, config.AIApiKey, config.AIModel,
                systemPrompt, userPrompt, ct);

            if (responseContent == null)
                return null;

            // 解析响应 JSON
            return _promptBuilder.ParseResponse(responseContent);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                                                ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 401/403：认证失败，严重错误，停止批次
            _logger.LogError("AI API 认证失败 (HTTP {Code}): 请检查 API Key 配置", (int)(ex.StatusCode ?? 0));
            _circuitBreaker.RecordFailure();
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // 429：触发服务端限流，等待 2 秒后重试一次
            _logger.LogWarning("AI API 服务端限流 (429)，等待 2 秒后重试...");
            try
            {
                await Task.Delay(2000, ct);
                var retryContent = await CallAiApiAsync(
                    url, config.AIApiKey, config.AIModel,
                    systemPrompt, userPrompt, ct);

                if (retryContent != null)
                    return _promptBuilder.ParseResponse(retryContent);
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx, "AI API 429 重试失败");
            }

            _circuitBreaker.RecordFailure();
            return null;
        }
        catch (HttpRequestException ex)
        {
            // 其他 HTTP 错误（5xx, timeout 等）
            _logger.LogWarning("AI API HTTP 错误: {Message}", ex.Message);
            _circuitBreaker.RecordFailure();
            return null;
        }
        catch (TaskCanceledException)
        {
            // HttpClient 超时（Timeout 设置所致）
            _logger.LogWarning("AI API 调用超时（{Timeout}秒）", ApiTimeout.TotalSeconds);
            _circuitBreaker.RecordFailure();
            return null;
        }
    }

    /// <summary>
    /// 发送 HTTP POST 请求到 AI API，返回响应体文本。
    /// </summary>
    /// <returns>成功时返回响应 JSON 字符串；失败时返回 null</returns>
    private async Task<string?> CallAiApiAsync(
        string url, string apiKey, string model,
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var request = BuildHttpRequest(url, apiKey, model, systemPrompt, userPrompt);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ApiTimeout); // 单次调用超时 30 秒

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            _logger.LogWarning("AI API 返回非成功状态: HTTP {Code}, Body={Body}",
                (int)statusCode, body.Length > 300 ? body[..300] + "..." : body);

            // 抛出 HttpRequestException 以便上层分类处理
            throw new HttpRequestException(
                $"AI API 返回 HTTP {(int)statusCode}: {body[..Math.Min(200, body.Length)]}",
                null, statusCode);
        }

        return body;
    }

    /// <summary>
    /// 构建 HTTP 请求消息（POST，JSON 内容类型，可选的 Authorization 头）。
    /// </summary>
    private HttpRequestMessage BuildHttpRequest(
        string url, string apiKey, string model,
        string systemPrompt, string userPrompt)
    {
        var payload = new AiChatCompletionRequest
        {
            Model = model,
            Messages = new List<AiChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt }
            },
            Temperature = 0.1,
            MaxTokens = 200
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // OpenAI 和 Custom 类型需要 API Key；Ollama 本地部署不需要
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    /// <summary>
    /// 将 AI 分析结果应用到文件决策缓存。
    /// 更新 AiLabel、AiConfidence、AiExplanation 字段，
    /// 然后通过 DecisionEngine.Arbitrate() 重新计算 FinalAction，
    /// 最后 Upsert 到数据库。
    /// </summary>
    private void ApplyResultToCache(
        FileDecisionCache cache, string label, double confidence, string explanation,
        bool aiEnabled, AITrustLevel trustLevel)
    {
        // 更新 AI 字段
        cache.AiLabel = label;
        cache.AiConfidence = confidence;
        cache.AiExplanation = explanation;

        // 通过决策引擎重新仲裁 FinalAction
        var rearbitrated = _decisionEngine.Arbitrate(cache, aiEnabled, trustLevel);

        // 持久化到仓储（非阻塞入队）
        _cacheRepo.Upsert(rearbitrated);

        _logger.LogDebug("AI 结果已写入缓存: {Path} → label={Label}, conf={Confidence:P}, action={Action}",
            cache.FilePath, label, confidence, rearbitrated.FinalAction);
    }

    // ============================================================
    // URL 构建
    // ============================================================

    /// <summary>
    /// 根据 AI 服务类型构建完整的 API 端点 URL。
    /// </summary>
    private string BuildApiUrl(string serviceType, string configuredUrl)
    {
        return serviceType.ToLowerInvariant() switch
        {
            "openai" => "https://api.openai.com/v1/chat/completions",
            "ollama" => BuildOllamaUrl(configuredUrl),
            "custom" => configuredUrl,         // 用户提供完整 URL
            _ => configuredUrl                  // 未知类型兜底
        };
    }

    /// <summary>
    /// 构建 Ollama API 端点 URL。
    /// Ollama ≥ 0.1.32 支持 /v1/chat/completions 端点（OpenAI 兼容）。
    /// </summary>
    private static string BuildOllamaUrl(string configuredUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? "http://localhost:11434"
            : configuredUrl.TrimEnd('/');

        // 若用户已配置完整端点路径，不再追加
        if (baseUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl + "/v1/chat/completions";
    }

    // ============================================================
    // 初始化
    // ============================================================

    /// <summary>后台加载日限历史数据（不阻塞主流程）</summary>
    private async Task LoadUsageAsync()
    {
        try
        {
            await _usageTracker.LoadAsync();
            _logger.LogDebug("AI 日限数据加载完成: {Used}/{Limit}", _usageTracker.UsedCount, _configService.Current.AIDailyLimit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 日限数据加载失败，默认从 0 开始");
        }
    }

    // ============================================================
    // IDisposable
    // ============================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rateLimiter.Dispose();
        _usageTracker.Dispose();
    }
}
