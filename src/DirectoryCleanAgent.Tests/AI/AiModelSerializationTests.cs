using System.Text.Json;
using DirectoryCleanAgent.AI.Models;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AI 模型序列化/反序列化测试 —— 验证 JSON 契约的正确性。
/// 覆盖请求体序列化、响应体反序列化、ExtractContent 提取逻辑、
/// AiAnalysisResult 工厂方法。
/// </summary>
public class AiModelSerializationTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ============================================================
    // AiChatCompletionRequest —— 请求序列化
    // ============================================================

    [Fact]
    public void AiChatCompletionRequest_SerializesToExpectedJson()
    {
        var request = new AiChatCompletionRequest
        {
            Model = "test-model",
            Messages = new List<AiChatMessage>
            {
                new() { Role = "system", Content = "你是一个助手" },
                new() { Role = "user", Content = "分析文件" }
            },
            Temperature = 0.1,
            MaxTokens = 200
        };

        var json = JsonSerializer.Serialize(request, CamelCaseOptions);

        // 验证 JSON 结构
        Assert.Contains("\"model\"", json);
        Assert.Contains("\"test-model\"", json);
        Assert.Contains("\"messages\"", json);
        Assert.Contains("\"system\"", json);
        Assert.Contains("\"user\"", json);
        Assert.Contains("\"temperature\"", json);
        Assert.Contains("\"max_tokens\"", json);
    }

    [Fact]
    public void AiChatCompletionRequest_DeserializesFromJson()
    {
        var json = """
        {
            "model": "gpt-4",
            "messages": [
                {"role": "system", "content": "系统提示"},
                {"role": "user", "content": "用户提示"}
            ],
            "temperature": 0.1,
            "max_tokens": 200
        }
        """;

        var request = JsonSerializer.Deserialize<AiChatCompletionRequest>(json, CamelCaseOptions);

        Assert.NotNull(request);
        Assert.Equal("gpt-4", request.Model);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("user", request.Messages[1].Role);
    }

    [Fact]
    public void AiChatCompletionRequest_DefaultValues()
    {
        var request = new AiChatCompletionRequest();

        Assert.Equal(0.1, request.Temperature);
        Assert.Equal(200, request.MaxTokens);
        Assert.Empty(request.Messages);
        Assert.Equal(string.Empty, request.Model);
    }

    // ============================================================
    // AiChatCompletionResponse —— 响应体反序列化
    // ============================================================

    [Fact]
    public void AiChatCompletionResponse_ExtractContent_ReturnsFirstChoiceMessage()
    {
        var json = AiTestDataFactory.ChatCompletionResponseJson("这是一条测试回复");

        var response = JsonSerializer.Deserialize<AiChatCompletionResponse>(json, CamelCaseOptions);

        Assert.NotNull(response);
        var content = response.ExtractContent();
        Assert.Equal("这是一条测试回复", content);
    }

    [Fact]
    public void AiChatCompletionResponse_ExtractContent_WithError_ReturnsNull()
    {
        var json = """{"id":"err-1","error":{"message":"API Key invalid","type":"auth_error","code":"invalid_api_key"}}""";

        var response = JsonSerializer.Deserialize<AiChatCompletionResponse>(json, CamelCaseOptions);

        Assert.NotNull(response);
        Assert.NotNull(response.Error);
        var content = response.ExtractContent();
        // 有 Error 时应返回 null
        Assert.Null(content);
    }

    [Fact]
    public void AiChatCompletionResponse_ExtractContent_EmptyChoices_ReturnsNull()
    {
        var json = """{"id":"test","choices":[]}""";

        var response = JsonSerializer.Deserialize<AiChatCompletionResponse>(json, CamelCaseOptions);

        Assert.NotNull(response);
        var content = response.ExtractContent();
        // 空 choices → FirstOrDefault 返回 null → null
        Assert.Null(content);
    }

    [Fact]
    public void AiChatCompletionResponse_ExtractContent_NullChoices_ReturnsNull()
    {
        var json = """{"id":"test"}""";

        var response = JsonSerializer.Deserialize<AiChatCompletionResponse>(json, CamelCaseOptions);

        Assert.NotNull(response);
        var content = response.ExtractContent();
        // null choices → null
        Assert.Null(content);
    }

    [Fact]
    public void AiChatCompletionResponse_ExtractContent_MessageWithMissingContent()
    {
        var json = """{"id":"test","choices":[{"index":0,"message":{"role":"assistant"},"finish_reason":"stop"}]}""";

        var response = JsonSerializer.Deserialize<AiChatCompletionResponse>(json, CamelCaseOptions);

        Assert.NotNull(response);
        var content = response.ExtractContent();
        // content 字段缺失 → 默认值 string.Empty → ExtractContent 返回 string.Empty
        Assert.Equal(string.Empty, content);
    }

    // ============================================================
    // AiModelResponse —— 默认值
    // ============================================================

    [Fact]
    public void AiModelResponse_DefaultValues()
    {
        var model = new AiModelResponse();

        Assert.Equal("unknown", model.Label);
        Assert.Equal(0.5, model.Confidence);
        Assert.Equal(string.Empty, model.Explanation);
    }

    // ============================================================
    // AiAnalysisResult —— 工厂方法
    // ============================================================

    [Fact]
    public void AiAnalysisResult_Factory_Failure_CreatesCorrectly()
    {
        var result = AiAnalysisResult.Failure(@"\\?\C:\fail.tmp", "网络超时");

        Assert.Equal(@"\\?\C:\fail.tmp", result.FilePath);
        Assert.False(result.IsSuccess);
        Assert.Equal("unknown", result.Label);
        Assert.Equal("网络超时", result.ErrorMessage);
    }

    [Fact]
    public void AiAnalysisResult_Factory_Success_CreatesCorrectly()
    {
        var result = AiAnalysisResult.Success(@"\\?\C:\ok.tmp", "safe", 0.98, "临时文件可删除");

        Assert.Equal(@"\\?\C:\ok.tmp", result.FilePath);
        Assert.True(result.IsSuccess);
        Assert.Equal("safe", result.Label);
        Assert.Equal(0.98, result.Confidence);
        Assert.Equal("临时文件可删除", result.Explanation);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void AiAnalysisResult_Success_ErrorMessageIsNull()
    {
        var result = AiAnalysisResult.Success(@"\\?\C:\file.tmp", "risky", 0.5, "测试");

        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void AiAnalysisResult_Failure_ConfidenceIsDefault()
    {
        var result = AiAnalysisResult.Failure(@"\\?\C:\fail.tmp", "错误");

        Assert.Equal(0.0, result.Confidence); // 失败时置信度为默认值 0
    }
}
