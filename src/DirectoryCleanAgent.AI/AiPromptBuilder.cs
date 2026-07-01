using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 提示词构建器与响应解析器。
///
/// 职责：
///   1. 构建发送给 AI 的系统提示词（定义角色、输出格式、标签含义）
///   2. 构建用户提示词（包含文件具体信息）
///   3. 解析 AI 返回的原始文本为结构化 AiModelResponse
///   4. 验证 AI 标签是否在合法值集合内
///
/// 无状态，线程安全。
/// </summary>
internal sealed class AiPromptBuilder
{
    private readonly ILogger<AiPromptBuilder> _logger;

    // 合法的 AI 标签值集合（与 B3 DecisionEngine 决策表对齐）
    private static readonly HashSet<string> ValidLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe", "unknown", "risky"
    };

    // 用于防御性解析的正则：匹配第一个 JSON 对象
    private static readonly Regex JsonObjectRegex = new(
        @"\{[^{}]*""label""[^{}]*""confidence""[^{}]*""explanation""[^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // JSON 反序列化选项：宽松模式（允许大小写不敏感 + 尾部逗号等）
    private static readonly JsonSerializerOptions LooseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AiPromptBuilder(ILogger<AiPromptBuilder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ============================================================
    // 系统提示词 —— 定义 AI 角色、输出格式、标签含义
    // ============================================================

    /// <summary>
    /// 构建系统提示词。
    /// 严格约束 AI 只输出 JSON，不输出额外文本。
    /// </summary>
    public string BuildSystemPrompt()
    {
        return """
        你是一个专业的磁盘清理顾问。你的任务是分析给定的文件，判断是否可以安全删除。

        ## 输出格式
        你必须只输出一个严格的 JSON 对象，不包含任何额外文本、markdown 标记或解释。

        ## JSON 字段说明
        - label: 文件安全标签，必须为以下三者之一：
          * "safe"    — 文件可以安全删除（如临时文件、缓存、日志等）
          * "unknown" — 无法确定，需要用户人工判断
          * "risky"   — 高风险，不应删除（如系统文件、配置文件、个人文档等）
        - confidence: 你的置信度，0.0 到 1.0 之间的浮点数
        - explanation: 简短的中文解释（20字以内），说明为什么给出此标签

        ## 分析原则
        1. 优先根据文件扩展名、路径和命名模式判断
        2. 系统目录（Windows、Program Files等）下的文件默认为 risky
        3. 临时目录（Temp、%TEMP%、.cache等）下的文件倾向于 safe
        4. 不确定时标记为 unknown，不要猜测
        5. 置信度反映你对判断的确信程度

        ## 输出示例
        {"label":"safe","confidence":0.95,"explanation":"Visual Studio编译临时文件"}
        {"label":"unknown","confidence":0.6,"explanation":"未知扩展名，路径不明确"}
        {"label":"risky","confidence":0.9,"explanation":"位于系统目录下的DLL文件"}
        """;
    }

    // ============================================================
    // 用户提示词 —— 包含待分析文件的具体信息
    // ============================================================

    /// <summary>
    /// 构建用户提示词，包含单个文件的详细信息。
    /// </summary>
    /// <param name="filePath">文件完整路径</param>
    /// <param name="sizeBytes">文件大小（字节）</param>
    /// <param name="lastWriteTime">最后修改时间</param>
    public string BuildUserPrompt(string filePath, long sizeBytes, DateTime lastWriteTime)
    {
        var fileName = Path.GetFileName(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? "未知";
        var sizeMb = sizeBytes / (1024.0 * 1024.0);
        var age = DateTime.UtcNow - lastWriteTime;

        return $"请分析以下文件：\n" +
               $"- 文件名：{fileName}\n" +
               $"- 所在目录：{directory}\n" +
               $"- 文件大小：{sizeMb:F1} MB\n" +
               $"- 最后修改：{lastWriteTime:yyyy-MM-dd HH:mm:ss} (UTC)\n" +
               $"- 距今：{(int)age.TotalDays} 天前";
    }

    // ============================================================
    // 连接测试提示词
    // ============================================================

    /// <summary>
    /// 构建 API 连接测试提示词。
    /// 简单问询，验证端点和密钥有效性。
    /// </summary>
    public (string SystemPrompt, string UserPrompt) BuildTestPrompt()
    {
        var systemPrompt = "你是一个测试助手。请用 JSON 格式回复：{\"status\":\"ok\"}";
        var userPrompt = "这是一条连接测试消息，请回复 {\"status\":\"ok\"}";
        return (systemPrompt, userPrompt);
    }

    // ============================================================
    // 响应解析 —— 防御性解析 AI 返回的原始文本
    // ============================================================

    /// <summary>
    /// 解析 AI 模型返回的原始文本为结构化的 AiModelResponse。
    ///
    /// 防御性策略（应对模型可能不严格遵循 JSON-only 指令的情况）：
    ///   1. 直接尝试用宽松的 JSON 选项反序列化整个文本
    ///   2. 失败则用正则提取第一个疑似 JSON 对象的子串
    ///   3. 失败则尝试去除 markdown 代码块标记（```json ... ```）后解析
    ///   4. 仍失败则返回 null —— 调用方应记录警告并跳过该文件
    /// </summary>
    /// <param name="rawContent">AI 返回的原始完整文本</param>
    /// <returns>解析成功的 AiModelResponse；完全无法解析时返回 null</returns>
    public AiModelResponse? ParseResponse(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            _logger.LogWarning("AI 返回内容为空");
            return null;
        }

        var content = rawContent.Trim();

        // 策略1：直接反序列化整个文本
        var result = TryDeserialize(content);
        if (result != null)
            return result;

        // 策略2：正则提取第一个 JSON 对象
        var match = JsonObjectRegex.Match(content);
        if (match.Success)
        {
            result = TryDeserialize(match.Value);
            if (result != null)
                return result;
        }

        // 策略3：去除 markdown 代码块标记后解析
        var stripped = StripMarkdownCodeBlock(content);
        if (stripped != content)
        {
            result = TryDeserialize(stripped);
            if (result != null)
                return result;

            // 在去除 markdown 的文本上也尝试正则
            var match2 = JsonObjectRegex.Match(stripped);
            if (match2.Success)
            {
                result = TryDeserialize(match2.Value);
                if (result != null)
                    return result;
            }
        }

        // 完全无法解析
        _logger.LogWarning("AI 响应解析失败，原始内容(前200字符): {Content}",
            content.Length > 200 ? content[..200] + "..." : content);
        return null;
    }

    /// <summary>尝试反序列化 JSON 字符串为 AiModelResponse</summary>
    private AiModelResponse? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AiModelResponse>(json, LooseJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 去除 markdown 代码块标记（```json ... ``` 或 ``` ... ```）。
    /// </summary>
    private static string StripMarkdownCodeBlock(string text)
    {
        var trimmed = text.Trim();

        // 匹配以 ``` 开头并以 ``` 结尾的代码块
        if (trimmed.StartsWith("```") && trimmed.EndsWith("```"))
        {
            // 找到第一个换行和最后一个 ``` 之间的内容
            var firstNewline = trimmed.IndexOf('\n');
            var lastBacktick = trimmed.LastIndexOf("```", StringComparison.Ordinal);

            if (firstNewline > 0 && lastBacktick > firstNewline)
            {
                var inner = trimmed[(firstNewline + 1)..lastBacktick].Trim();
                return inner;
            }
        }

        return text;
    }

    // ============================================================
    // 标签验证
    // ============================================================

    /// <summary>
    /// 验证 AI 标签是否在合法值集合内。
    /// 不合法时校正为 "unknown"（保守策略）并记录警告。
    /// </summary>
    /// <param name="label">AI 返回的原始标签值</param>
    /// <returns>校正后的合法标签值</returns>
    public string ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            _logger.LogWarning("AI 返回空标签，校正为 'unknown'");
            return "unknown";
        }

        var trimmed = label.Trim().ToLowerInvariant();

        if (ValidLabels.Contains(trimmed))
            return trimmed;

        _logger.LogWarning("AI 返回未识别的标签 '{Raw}'，校正为 'unknown'（合法值: safe/unknown/risky）", label);
        return "unknown";
    }
}
