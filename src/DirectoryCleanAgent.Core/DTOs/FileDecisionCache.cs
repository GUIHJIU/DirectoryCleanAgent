namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 文件决策缓存实体。
/// 仅存储被规则引擎命中或用户标记的文件，不存储全盘文件元数据。
/// 以 file_path 为主键，通过 cache_version 与当前规则版本比对判断有效性。
/// 对应 SQLite 的 FileDecisionCache 表。
/// </summary>
public record FileDecisionCache
{
    /// <summary>文件路径（\\?\ 格式），主键</summary>
    public required string FilePath { get; init; }

    /// <summary>文件大小（字节）</summary>
    public required long SizeBytes { get; init; }

    /// <summary>文件最后写入时间</summary>
    public required DateTime LastWriteTime { get; init; }

    /// <summary>规则引擎裁决结果</summary>
    public required RuleVerdict RuleVerdict { get; init; }

    /// <summary>
    /// 语义分类标签（如"系统临时文件"、"应用缓存"等）。
    /// 简单模式下用于替代具体规则名称展示。
    /// </summary>
    public required string SemanticCategory { get; init; }

    /// <summary>AI 顾问给出的标签（safe / unknown / risky），未调用 AI 时为 null</summary>
    public string? AiLabel { get; set; }

    /// <summary>AI 标签的置信度（0.0 ~ 1.0），未调用 AI 时为 null</summary>
    public double? AiConfidence { get; set; }

    /// <summary>AI 给出的分类解释文本</summary>
    public string? AiExplanation { get; set; }

    /// <summary>最终操作决策（由决策引擎综合规则裁决和 AI 标签后仲裁）</summary>
    public required FinalAction FinalAction { get; set; }

    /// <summary>用户手动做出的决策覆盖（如用户勾选/取消勾选），无用户干预时为 null</summary>
    public FinalAction? UserDecision { get; set; }

    /// <summary>
    /// 生成此缓存时的规则版本号。
    /// 启动时若此值与 UserConfig.RuleCacheVersion 不一致，则清空缓存并重新裁决。
    /// </summary>
    public required int CacheVersion { get; init; }
}
