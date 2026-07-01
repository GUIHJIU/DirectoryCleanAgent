using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.AI.Models;

// ============================================================
// OpenAI Chat Completions API 响应体（兼容多服务端）
// ============================================================

/// <summary>
/// Chat Completions API 完整响应体。
/// 包含 choices 数组（正常响应）或 error 对象（异常响应）。
/// </summary>
internal sealed class AiChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("choices")]
    public List<AiChatChoice>? Choices { get; set; }

    [JsonPropertyName("error")]
    public AiApiError? Error { get; set; }

    /// <summary>
    /// 提取第一个 choice 的 message.content。
    /// 无有效内容时返回 null。
    /// </summary>
    public string? ExtractContent()
    {
        if (Error != null)
            return null;

        var firstChoice = Choices?.FirstOrDefault();
        return firstChoice?.Message?.Content;
    }
}

/// <summary>
/// 单个 choice 项，包含模型生成的 message 和结束原因。
/// </summary>
internal sealed class AiChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public AiChatMessage? Message { get; set; }

    /// <summary>结束原因：stop / length / content_filter</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>
/// API 错误响应体（OpenAI 标准 error 结构）。
/// </summary>
internal sealed class AiApiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

// ============================================================
// AI 模型返回的结构化分析结果（内部分析用）
// ============================================================

/// <summary>
/// AI 模型输出的结构化 JSON 反序列化目标。
/// 模型被指示严格输出：{"label":"safe|unknown|risky","confidence":0.0-1.0,"explanation":"..."}
/// </summary>
internal sealed class AiModelResponse
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "unknown";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.5;

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;
}

// ============================================================
// 对外公开的分析结果 DTO
// ============================================================

/// <summary>
/// AI 分析单个文件的最终结果，对外公开。
/// 包含原始文件路径、解析后的 AI 标签、置信度和解释文本。
/// </summary>
public sealed class AiAnalysisResult
{
    /// <summary>文件完整路径（\\?\ 格式）</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>AI 标签：safe（安全可删）/ unknown（待确认）/ risky（高风险）</summary>
    public string Label { get; init; } = "unknown";

    /// <summary>AI 置信度（0.0 ~ 1.0）</summary>
    public double Confidence { get; init; }

    /// <summary>AI 给出的分类解释文本（中文）</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>本次分析是否成功完成</summary>
    public bool IsSuccess { get; init; }

    /// <summary>失败时的错误信息，成功时为 null</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>创建失败结果</summary>
    public static AiAnalysisResult Failure(string filePath, string errorMessage) => new()
    {
        FilePath = filePath,
        Label = "unknown",
        IsSuccess = false,
        ErrorMessage = errorMessage
    };

    /// <summary>创建成功结果</summary>
    public static AiAnalysisResult Success(
        string filePath, string label, double confidence, string explanation) => new()
    {
        FilePath = filePath,
        Label = label,
        Confidence = confidence,
        Explanation = explanation,
        IsSuccess = true
    };
}
