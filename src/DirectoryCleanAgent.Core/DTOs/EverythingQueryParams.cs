namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// Everything 查询参数封装。
/// 将 UI 层的排序/分组/过滤请求转译为 Everything SDK 原生查询参数，
/// 实现排序下推，避免内存中排序大量文件。
/// </summary>
public record EverythingQueryParams
{
    /// <summary>
    /// Everything 搜索表达式（如 "ext:log size:>100mb"），默认为空表示返回所有文件。
    /// 由 UI 层的分组/筛选条件转换而来，直接传递给 Everything IPC。
    /// </summary>
    public string? SearchExpression { get; init; }

    /// <summary>
    /// Everything 原生排序类型。
    /// 映射自设计文档补充说明 4.7 节的排序参数表。
    /// 为 null 时使用 Everything 默认排序（通常按名称）。
    /// </summary>
    public EverythingSortType? SortType { get; init; }

    /// <summary>是否降序排列，默认升序</summary>
    public bool SortDescending { get; init; } = false;

    /// <summary>
    /// 要扫描的卷列表（盘符，如 "C:"）。为 null 时行为由调用方决定——
    /// UI 层默认回退为 ["C:"]（取自 UserConfig.IncludedVolumes），其他调用方需自行提供默认值。
    /// </summary>
    public IReadOnlyList<string>? Volumes { get; init; }

    /// <summary>路径过滤前缀（\\?\ 格式），仅返回该路径下的文件，为 null 表示不限制</summary>
    public string? PathFilter { get; init; }

    /// <summary>返回结果的最大数量，为 null 表示不限制（由 Everything 默认限制）</summary>
    public int? MaxResults { get; init; }
}

/// <summary>
/// Everything 原生排序字段枚举。
/// 映射到 Everything SDK 的排序常量，支持列头点击排序。
/// 注意：语义标签排序无法委托给 Everything（标签是规则引擎产出），仅在缓存子集内完成。
/// </summary>
public enum EverythingSortType
{
    /// <summary>按文件名排序</summary>
    Name,

    /// <summary>按文件路径排序</summary>
    Path,

    /// <summary>按文件大小排序</summary>
    Size,

    /// <summary>按修改日期排序</summary>
    DateModified,

    /// <summary>按扩展名排序</summary>
    Extension
}
