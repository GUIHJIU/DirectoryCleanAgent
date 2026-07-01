using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.Rules.Models;

/// <summary>
/// 启发式规则 JSON 定义 DTO — 用于反序列化启发式规则配置文件。
///
/// 每个规则包含一组条件（extensions / pathPatterns / minSizeBytes / minAgeDays 等），
/// 文件必须同时满足所有非 null 条件才算命中。
///
/// JSON 示例：
/// {
///   "name": "large_old_log_files",
///   "priority": 5,
///   "semanticCategory": "日志/转储",
///   "reason": "大于100MB且90天未访问的日志文件",
///   "conditions": {
///     "extensions": [".log"],
///     "minSizeBytes": 104857600,
///     "minAgeDays": 90
///   }
/// }
/// </summary>
public sealed class HeuristicRuleDefinition
{
    /// <summary>规则唯一标识名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>规则优先级：5=建议删除，6=建议保留</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    /// <summary>语义分类标签</summary>
    [JsonPropertyName("semanticCategory")]
    public string SemanticCategory { get; set; } = string.Empty;

    /// <summary>规则简短解释</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>规则是否启用（禁用后不参与匹配）</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>匹配条件组</summary>
    [JsonPropertyName("conditions")]
    public RuleConditions Conditions { get; set; } = new();
}

/// <summary>
/// 规则匹配条件 — 所有非 null 条件必须同时满足。
/// 如所有条件字段均为 null，则该规则匹配所有文件（危险，应避免）。
/// </summary>
public sealed class RuleConditions
{
    /// <summary>匹配的文件扩展名列表（小写，含点号），如 [".log", ".dmp"]，null 表示不限制</summary>
    [JsonPropertyName("extensions")]
    public List<string>? Extensions { get; set; }

    /// <summary>匹配的路径 Glob 模式列表（如 "**\\Temp\\**"），null 表示不限制</summary>
    [JsonPropertyName("pathPatterns")]
    public List<string>? PathPatterns { get; set; }

    /// <summary>
    /// 预编译的路径 Glob 模式匹配器列表（JsonIgnore — 运行时编译，不序列化）。
    /// 在 HeuristicRuleLoader 加载规则后统一编译，存入此字段。
    /// 评估阶段直接使用预编译实例，避免每文件重新编译正则。
    /// </summary>
    [JsonIgnore]
    public List<CompiledGlobPattern>? CompiledPathPatterns { get; set; }

    /// <summary>文件大小下限（字节），含此值，null 表示不限制</summary>
    [JsonPropertyName("minSizeBytes")]
    public long? MinSizeBytes { get; set; }

    /// <summary>文件大小上限（字节），含此值，null 表示不限制</summary>
    [JsonPropertyName("maxSizeBytes")]
    public long? MaxSizeBytes { get; set; }

    /// <summary>文件最后写入距今的最小天数，null 表示不限制</summary>
    [JsonPropertyName("minAgeDays")]
    public int? MinAgeDays { get; set; }

    /// <summary>文件最后写入距今的最大天数，null 表示不限制</summary>
    [JsonPropertyName("maxAgeDays")]
    public int? MaxAgeDays { get; set; }
}

/// <summary>
/// 启发式规则配置文件根对象。
/// </summary>
public sealed class HeuristicRuleConfigFile
{
    /// <summary>配置文件版本号，用于向前兼容</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>规则列表</summary>
    [JsonPropertyName("rules")]
    public List<HeuristicRuleDefinition> Rules { get; set; } = new();
}
