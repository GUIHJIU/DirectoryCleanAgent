namespace DirectoryCleanAgent.Core.Constants;

/// <summary>
/// AI 顾问标签常量 — B3（决策引擎）与 B7（AI 顾问）之间的字符串契约。
///
/// 契约约定：
///   B7 AI 顾问必须返回以下三个值之一（大小写不敏感）。
///   若返回未识别的标签值，B3 决策引擎将回退到 ManualReview。
///
/// 使用方式：
///   if (aiLabel.Equals(AiLabelConstants.Safe, StringComparison.OrdinalIgnoreCase)) { ... }
/// </summary>
public static class AiLabelConstants
{
    /// <summary>AI 认为文件安全，可以删除</summary>
    public const string Safe = "safe";

    /// <summary>AI 无法确定文件性质</summary>
    public const string Unknown = "unknown";

    /// <summary>AI 认为文件有风险，不应自动删除</summary>
    public const string Risky = "risky";

    /// <summary>验证标签是否为已知值（大小写不敏感）。用于诊断和日志。</summary>
    /// <param name="label">AI 标签字符串（可为 null）</param>
    /// <returns>true 表示该标签可被决策表识别</returns>
    public static bool IsKnownLabel(string? label) =>
        label != null && (
            label.Equals(Safe, StringComparison.OrdinalIgnoreCase) ||
            label.Equals(Unknown, StringComparison.OrdinalIgnoreCase) ||
            label.Equals(Risky, StringComparison.OrdinalIgnoreCase));
}
