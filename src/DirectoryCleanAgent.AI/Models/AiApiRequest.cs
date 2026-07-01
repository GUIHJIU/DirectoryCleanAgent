using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.AI.Models;

/// <summary>
/// OpenAI Chat Completions API 请求体（兼容 OpenAI / Ollama / 自定义 API）。
/// 单次请求携带一个文件的系统提示词 + 用户提示词。
/// </summary>
internal sealed class AiChatCompletionRequest
{
    /// <summary>模型名称（如 gpt-3.5-turbo / llama3 / qwen2.5）</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>消息列表：系统提示词 + 用户提示词</summary>
    [JsonPropertyName("messages")]
    public List<AiChatMessage> Messages { get; set; } = new();

    /// <summary>采样温度：0.1 偏向确定性输出，减少幻觉</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    /// <summary>最大输出 Token 数：200 足够输出 JSON + 简短解释</summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 200;
}

/// <summary>
/// Chat Completions 消息体，对应 OpenAI 标准 messages 数组元素。
/// </summary>
internal sealed class AiChatMessage
{
    /// <summary>角色：system / user / assistant</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>消息文本内容</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
