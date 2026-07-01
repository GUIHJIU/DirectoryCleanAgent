using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AI 模块测试数据工厂，提供可复用的测试数据与快捷构造方法。
/// 所有数据基于固定时间戳确保测试结果可重复。
/// </summary>
public static class AiTestDataFactory
{
    /// <summary>固定参考时间（UTC），保证测试确定性</summary>
    public static readonly DateTime FixedUtcNow = new(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>默认 AI 配置：启用、自定义 API、信任度中</summary>
    public static UserConfig CreateDefaultConfig() => new()
    {
        AIEnabled = true,
        AIApiUrl = "https://test-api.example.com/v1/chat/completions",
        AIApiKey = "test-api-key-12345",
        AIModel = "test-model-v1",
        AITrustLevel = AITrustLevel.Medium,
        AIDailyLimit = 50,
        AIServiceType = "custom"
    };

    /// <summary>创建禁用 AI 的配置</summary>
    public static UserConfig CreateDisabledConfig()
    {
        var config = CreateDefaultConfig();
        config.AIEnabled = false;
        return config;
    }

    /// <summary>创建带 AI 字段的文件决策缓存条目</summary>
    public static FileDecisionCache CreateCache(
        string? filePath = null,
        RuleVerdict verdict = RuleVerdict.SuggestDelete,
        long sizeBytes = 1_000_000,
        DateTime? lastWriteTime = null,
        string? aiLabel = null,
        double? aiConfidence = null,
        string? semanticCategory = "Uncategorized",
        FinalAction? finalAction = null,
        int cacheVersion = 1)
    {
        return new FileDecisionCache
        {
            FilePath = filePath ?? @"\\?\C:\Temp\test_file.tmp",
            SizeBytes = sizeBytes,
            LastWriteTime = lastWriteTime ?? FixedUtcNow.AddDays(-30),
            RuleVerdict = verdict,
            SemanticCategory = semanticCategory ?? "Uncategorized",
            AiLabel = aiLabel,
            AiConfidence = aiConfidence,
            FinalAction = finalAction ?? FinalAction.SuggestDelete,
            CacheVersion = cacheVersion
        };
    }

    /// <summary>批量创建指定数量的缓存条目</summary>
    public static List<FileDecisionCache> CreateCacheBatch(int count, long baseSizeBytes = 1_000_000)
    {
        var list = new List<FileDecisionCache>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(CreateCache(
                filePath: $@"\\?\C:\Temp\batch_file_{i:D4}.tmp",
                sizeBytes: baseSizeBytes + i * 1024));
        }
        return list;
    }

    /// <summary>创建 AI 分析成功结果</summary>
    public static AiAnalysisResult CreateSuccessResult(
        string filePath = @"\\?\C:\test.tmp",
        string label = "safe",
        double confidence = 0.95,
        string explanation = "测试成功结果")
    {
        return AiAnalysisResult.Success(filePath, label, confidence, explanation);
    }

    /// <summary>创建 AI 分析失败结果</summary>
    public static AiAnalysisResult CreateFailureResult(
        string filePath = @"\\?\C:\test.tmp",
        string errorMessage = "测试失败结果")
    {
        return AiAnalysisResult.Failure(filePath, errorMessage);
    }

    /// <summary>生成有效的 AI API 响应 JSON 字符串</summary>
    public static string ValidApiResponseJson(
        string label = "safe",
        double confidence = 0.95,
        string explanation = "测试解释文本")
    {
        return $"{{\"label\":\"{label}\",\"confidence\":{confidence},\"explanation\":\"{explanation}\"}}";
    }

    /// <summary>生成带 markdown 代码块包裹的 AI 响应</summary>
    public static string MarkdownWrappedApiResponseJson(
        string label = "safe",
        double confidence = 0.9,
        string explanation = "Markdown包裹测试")
    {
        var json = ValidApiResponseJson(label, confidence, explanation);
        return $"```json\n{json}\n```";
    }

    /// <summary>生成带额外文本前缀的 AI 响应（测试正则提取）</summary>
    public static string TextPrefixApiResponseJson(
        string label = "risky",
        double confidence = 0.8,
        string explanation = "前缀测试")
    {
        var json = ValidApiResponseJson(label, confidence, explanation);
        return $"这是我对文件的分析：{json}，请参考以上信息。";
    }

    /// <summary>创建模拟的 API 聊天完成响应 JSON</summary>
    public static string ChatCompletionResponseJson(string content)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(content);
        return $"{{\"id\":\"chatcmpl-test123\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{escaped}}},\"finish_reason\":\"stop\"}}]}}";
    }
}
