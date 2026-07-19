using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AiAdvisorService HTTP 层测试 —— 通过 Mock HttpMessageHandler 控制 HTTP 响应，
/// 验证 401/403/429 重试/5xx/超时 等场景的错误处理逻辑。
/// 使用真实 AI 内部组件（限流器、熔断器、提示构建器、用量追踪器）。
/// </summary>
public class AiAdvisorServiceHttpTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IConfigService> _configMock;
    private readonly Mock<IFileDecisionCacheRepository> _cacheRepoMock;
    private readonly Mock<IDecisionEngine> _decisionEngineMock;
    private readonly ILogger<AiAdvisorService> _logger;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly UserConfig _config;

    public AiAdvisorServiceHttpTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AiHttpTest_{Guid.NewGuid():N}");

        _config = AiTestDataFactory.CreateDefaultConfig();
        _configMock = new Mock<IConfigService>();
        _configMock.Setup(c => c.Current).Returns(_config);

        _cacheRepoMock = new Mock<IFileDecisionCacheRepository>();
        _cacheRepoMock.Setup(r => r.GetByFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileDecisionCache?)null);

        _decisionEngineMock = new Mock<IDecisionEngine>();
        _decisionEngineMock.Setup(d => d.Arbitrate(It.IsAny<FileDecisionCache>(), It.IsAny<bool>(), It.IsAny<AITrustLevel>()))
            .Returns((FileDecisionCache c, bool ai, AITrustLevel t) => c);

        _logger = NullLoggerFactory.Instance.CreateLogger<AiAdvisorService>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    /// <summary>创建带有模拟 HttpClient 的 AiAdvisorService</summary>
    private AiAdvisorService CreateService(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseContent = "",
        Action<HttpRequestMessage>? requestValidator = null)
    {
        var httpClient = CreateMockHttpClient(statusCode, responseContent, requestValidator);

        return new AiAdvisorService(
            _configMock.Object,
            _cacheRepoMock.Object,
            _decisionEngineMock.Object,
            _logger,
            new AiRateLimiter(NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>()),
            new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
            new AiDailyUsageTracker(_tempDir, 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
            new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
            httpClient);
    }

    /// <summary>创建模拟 HttpClient</summary>
    private HttpClient CreateMockHttpClient(
        HttpStatusCode statusCode,
        string responseContent,
        Action<HttpRequestMessage>? requestValidator = null)
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                requestValidator?.Invoke(req);
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseContent)
            });

        return new HttpClient(_httpHandlerMock.Object);
    }

    // ============================================================
    // HTTP 200 —— 正常响应
    // ============================================================

    [Fact]
    public async Task AnalyzeFileAsync_Http200_ReturnsSuccessResult()
    {
        // HTTP 响应体是完整的 Chat Completions 格式，模型输出在 choices[0].message.content 中
        var modelOutput = AiTestDataFactory.ValidApiResponseJson("safe", 0.95, "临时文件");
        var responseJson = AiTestDataFactory.ChatCompletionResponseJson(modelOutput);
        var service = CreateService(HttpStatusCode.OK, responseJson);

        // 创建临时文件供 AnalyzeFileAsync 检查文件存在性
        var tempFile = Path.Combine(_tempDir, "test_http200.tmp");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(tempFile, "test content");

        var result = await service.AnalyzeFileAsync(tempFile);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal("safe", result.Label);
    }

    // ============================================================
    // HTTP 401/403 —— 认证失败
    // ============================================================

    [Fact]
    public async Task AnalyzeFilesAsync_Http401_ReturnsFailureResults()
    {
        var service = CreateService(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key"}}""");
        // 初始化日限追踪器
        var trackerLogger = NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>();
        var usageTracker = new AiDailyUsageTracker(_tempDir, 50, trackerLogger);
        await usageTracker.LoadAsync();

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        // 401 应返回失败结果
        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    [Fact]
    public async Task AnalyzeFilesAsync_Http403_ReturnsFailureResults()
    {
        var service = CreateService(HttpStatusCode.Forbidden, """{"error":{"message":"Forbidden"}}""");

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    // ============================================================
    // HTTP 429 —— 服务端限流重试
    // ============================================================

    [Fact]
    public async Task AnalyzeFilesAsync_Http429_RecordsFailure()
    {
        // 429 后应调用 RecordFailure
        var service = CreateService(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit exceeded"}}""");

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        // 429 重试一次后仍失败 → 返回失败
        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    // ============================================================
    // HTTP 5xx —— 服务器错误
    // ============================================================

    [Fact]
    public async Task AnalyzeFilesAsync_Http500_ReturnsFailureResults()
    {
        var service = CreateService(HttpStatusCode.InternalServerError, "Server Error");

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    [Fact]
    public async Task AnalyzeFilesAsync_Http503_ReturnsFailureResults()
    {
        var service = CreateService(HttpStatusCode.ServiceUnavailable, "Service Unavailable");

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    // ============================================================
    // HTTP 超时
    // ============================================================

    [Fact]
    public async Task AnalyzeFilesAsync_Timeout_ReturnsFailureResults()
    {
        // 模拟超时：HttpMessageHandler 抛出 TaskCanceledException
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to timeout"));

        var httpClient = new HttpClient(_httpHandlerMock.Object);
        var service = new AiAdvisorService(
            _configMock.Object, _cacheRepoMock.Object, _decisionEngineMock.Object,
            _logger,
            new AiRateLimiter(NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>()),
            new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
            new AiDailyUsageTracker(_tempDir, 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
            new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
            httpClient);

        var cache = AiTestDataFactory.CreateCache();
        var result = await service.AnalyzeFilesAsync(new List<FileDecisionCache> { cache });

        Assert.NotEmpty(result);
        Assert.False(result[0].IsSuccess);
    }

    // ============================================================
    // 请求验证
    // ============================================================

    [Fact]
    public async Task BuildHttpRequest_IncludesAuthorizationHeader()
    {
        string? authHeader = null;
        var modelOutput = AiTestDataFactory.ValidApiResponseJson();
        var responseJson = AiTestDataFactory.ChatCompletionResponseJson(modelOutput);
        var service = CreateService(HttpStatusCode.OK, responseJson,
            requestValidator: req =>
            {
                authHeader = req.Headers.Authorization?.ToString();
            });

        // 创建临时文件
        var tempFile = Path.Combine(_tempDir, "test_auth.tmp");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(tempFile, "test");

        await service.AnalyzeFileAsync(tempFile);

        Assert.NotNull(authHeader);
        Assert.StartsWith("Bearer ", authHeader);
    }

    [Fact]
    public async Task BuildHttpRequest_IncludesJsonContentType()
    {
        string? contentType = null;
        var modelOutput = AiTestDataFactory.ValidApiResponseJson();
        var responseJson = AiTestDataFactory.ChatCompletionResponseJson(modelOutput);
        var service = CreateService(HttpStatusCode.OK, responseJson,
            requestValidator: req =>
            {
                contentType = req.Content?.Headers.ContentType?.MediaType;
            });

        // 创建临时文件
        var tempFile = Path.Combine(_tempDir, "test_content_type.tmp");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(tempFile, "test");

        await service.AnalyzeFileAsync(tempFile);

        Assert.Equal("application/json", contentType);
    }

    // ============================================================
    // TestConnectionAsync
    // ============================================================

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsTrue()
    {
        var responseJson = AiTestDataFactory.ChatCompletionResponseJson("""{"status":"ok"}""");
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, responseJson);
        var rateLimiterLogger = NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>();

        var service = new AiAdvisorService(
            _configMock.Object, _cacheRepoMock.Object, _decisionEngineMock.Object,
            _logger,
            new AiRateLimiter(rateLimiterLogger),
            new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
            new AiDailyUsageTracker(_tempDir, 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
            new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
            httpClient);

        var result = await service.TestConnectionAsync(
            "custom", "https://test.example.com/v1", "key123", "model-v1");

        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_NonSuccess_ReturnsFalse()
    {
        var httpClient = CreateMockHttpClient(HttpStatusCode.BadRequest, "Bad Request");
        var rateLimiterLogger = NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>();

        var service = new AiAdvisorService(
            _configMock.Object, _cacheRepoMock.Object, _decisionEngineMock.Object,
            _logger,
            new AiRateLimiter(rateLimiterLogger),
            new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
            new AiDailyUsageTracker(_tempDir, 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
            new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
            httpClient);

        var result = await service.TestConnectionAsync(
            "custom", "https://test.example.com/v1", "key123", "model-v1");

        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_Timeout_ReturnsFalse()
    {
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Timeout"));

        var httpClient = new HttpClient(_httpHandlerMock.Object);
        var rateLimiterLogger = NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>();

        var service = new AiAdvisorService(
            _configMock.Object, _cacheRepoMock.Object, _decisionEngineMock.Object,
            _logger,
            new AiRateLimiter(rateLimiterLogger),
            new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
            new AiDailyUsageTracker(_tempDir, 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
            new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
            httpClient);

        var result = await service.TestConnectionAsync(
            "custom", "https://test.example.com/v1", "key123", "model-v1");

        Assert.False(result);
    }

    // ============================================================
    // URL 构建（通过反射验证）
    // ============================================================

    [Fact]
    public void BuildApiUrl_OpenAIType_ReturnsCorrectUrl()
    {
        var result = InvokeBuildApiUrl("openai", "https://custom.example.com");
        Assert.Equal("https://api.openai.com/v1/chat/completions", result);
    }

    [Fact]
    public void BuildApiUrl_OllamaType_DefaultUrl()
    {
        var result = InvokeBuildApiUrl("ollama", "");
        Assert.Equal("http://localhost:11434/v1/chat/completions", result);
    }

    [Fact]
    public void BuildApiUrl_OllamaType_CustomUrl()
    {
        var result = InvokeBuildApiUrl("ollama", "http://192.168.1.100:11434");
        Assert.Equal("http://192.168.1.100:11434/v1/chat/completions", result);
    }

    [Fact]
    public void BuildApiUrl_OllamaType_AlreadyHasEndpoint()
    {
        var result = InvokeBuildApiUrl("ollama", "http://localhost:11434/v1/chat/completions");
        Assert.Equal("http://localhost:11434/v1/chat/completions", result);
    }

    [Fact]
    public void BuildApiUrl_CustomType_ReturnsConfiguredUrl()
    {
        var result = InvokeBuildApiUrl("custom", "https://my-api.example.com/v1/chat");
        Assert.Equal("https://my-api.example.com/v1/chat", result);
    }

    [Fact]
    public void BuildApiUrl_UnknownType_FallsBackToConfiguredUrl()
    {
        var result = InvokeBuildApiUrl("unknown_type", "https://fallback.example.com/api");
        Assert.Equal("https://fallback.example.com/api", result);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>通过反射调用私有方法 BuildApiUrl</summary>
    private static string InvokeBuildApiUrl(string serviceType, string configuredUrl)
    {
        var method = typeof(AiAdvisorService).GetMethod("BuildApiUrl",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // 创建最小实例仅用于静态 URL 测试
        var mockConfig = new Mock<IConfigService>();
        mockConfig.Setup(c => c.Current).Returns(AiTestDataFactory.CreateDefaultConfig());

        var service = (AiAdvisorService)Activator.CreateInstance(
            typeof(AiAdvisorService),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[]
            {
                mockConfig.Object,
                new Mock<IFileDecisionCacheRepository>().Object,
                new Mock<IDecisionEngine>().Object,
                NullLoggerFactory.Instance.CreateLogger<AiAdvisorService>(),
                new AiRateLimiter(NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>()),
                new AiCircuitBreaker(NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>()),
                new AiDailyUsageTracker(Path.GetTempPath(), 50, NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>()),
                new AiPromptBuilder(NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>()),
                new HttpClient()
            },
            null)!;

        return (string)method.Invoke(service, new object[] { serviceType, configuredUrl })!;
    }
}
