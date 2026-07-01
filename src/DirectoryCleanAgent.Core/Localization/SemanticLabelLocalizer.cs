using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.Core.Localization;

/// <summary>
/// 语义标签本地化映射器。
/// 将 SemanticCategory 枚举值映射为当前语言的显示文本。
/// 对应设计文档 3.3 节定义的 8 个语义标签的中英文对照。
///
/// 与 ILocalizationService 的关系：
///   SemanticLabelLocalizer 是语义标签的专用本地化器，维护枚举→文本的硬编码映射表。
///   通用 UI 文本（按钮、标签、提示等）通过 ILocalizationService + .resx 管理。
///   两者分离的原因是：语义标签需要枚举类型安全 + 跨模块零依赖。
/// </summary>
public class SemanticLabelLocalizer : IDisposable
{
    private readonly ILogger<SemanticLabelLocalizer> _logger;
    private readonly ILocalizationService _localizationService;
    private string _currentLanguage;

    /// <summary>
    /// 语义标签映射表：枚举值 → (中文, English)
    /// 使用硬编码映射而非 .resx 资源，确保编译时完整性检查。
    /// </summary>
    private static readonly Dictionary<SemanticCategory, (string Zh, string En)> LabelMap = new()
    {
        [SemanticCategory.SystemTempFiles] = ("系统临时文件", "System Temporary Files"),
        [SemanticCategory.AppCache] = ("应用缓存", "Application Cache"),
        [SemanticCategory.OldDownloads] = ("下载旧文件", "Old Downloads"),
        [SemanticCategory.RecycleBin] = ("回收站", "Recycle Bin"),
        [SemanticCategory.LogsAndDumps] = ("日志/转储", "Logs / Dumps"),
        [SemanticCategory.OldUserFiles] = ("用户旧文件", "Old User Files"),
        [SemanticCategory.Protected] = ("保护内容", "Protected Content"),
        [SemanticCategory.Uncategorized] = ("未分类", "Uncategorized"),
    };

    /// <summary>
    /// 初始化语义标签本地化器。
    /// </summary>
    /// <param name="logger">结构化日志记录器</param>
    /// <param name="localizationService">本地化服务（用于同步语言状态）</param>
    public SemanticLabelLocalizer(
        ILogger<SemanticLabelLocalizer> logger,
        ILocalizationService localizationService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizationService = localizationService
            ?? throw new ArgumentNullException(nameof(localizationService));

        _currentLanguage = _localizationService.CurrentLanguage;

        // 订阅语言切换事件，实时更新当前语言
        _localizationService.LanguageChanged += OnLanguageChanged;

        _logger.LogInformation(
            "语义标签本地化器初始化完成。当前语言={Lang}, 标签数量={Count}",
            _currentLanguage, LabelMap.Count);
    }

    /// <summary>
    /// 获取指定语义分类在当前语言下的显示文本。
    /// </summary>
    /// <param name="category">语义分类枚举值</param>
    /// <returns>本地化后的语义标签文本</returns>
    public string Localize(SemanticCategory category)
    {
        try
        {
            if (!LabelMap.TryGetValue(category, out var labels))
            {
                _logger.LogWarning(
                    "语义分类 {Category} 缺少映射文本，返回枚举名称",
                    category);
                return category.ToString();
            }

            return IsEnglish(_currentLanguage) ? labels.En : labels.Zh;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "语义标签本地化失败: Category={Category}, Lang={Lang}",
                category, _currentLanguage);
            return category.ToString();
        }
    }

    /// <summary>
    /// 从字符串语义标签（如 FileDecisionCache.SemanticCategory）本地化为当前语言文本。
    /// 内部尝试将字符串解析为 SemanticCategory 枚举后映射；若解析失败，返回原字符串。
    /// 此方法兼容现有数据层以 string 存储语义标签的设计。
    /// </summary>
    /// <param name="semanticCategory">语义标签字符串（如 "系统临时文件"、"应用缓存"）</param>
    /// <returns>当前语言下的显示文本</returns>
    public string LocalizeFromString(string semanticCategory)
    {
        if (string.IsNullOrEmpty(semanticCategory))
        {
            return string.Empty;
        }

        // 尝试从原始中文标签反查枚举值
        if (TryParseCategory(semanticCategory, out var category))
        {
            return Localize(category);
        }

        // 无法解析，返回原字符串（可能是自定义规则产出的非标准标签）
        _logger.LogDebug("无法解析语义标签字符串: '{Label}'，按原样返回", semanticCategory);
        return semanticCategory;
    }

    /// <summary>
    /// 获取指定语义分类在指定语言下的显示文本。
    /// </summary>
    /// <param name="category">语义分类枚举值</param>
    /// <param name="cultureCode">语言代码（"zh-CN" 或 "en-US"）</param>
    /// <returns>本地化后的语义标签文本</returns>
    public string Localize(SemanticCategory category, string cultureCode)
    {
        try
        {
            if (!LabelMap.TryGetValue(category, out var labels))
            {
                return category.ToString();
            }

            return IsEnglish(cultureCode) ? labels.En : labels.Zh;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "语义标签指定语言本地化失败: Category={Category}, Code={Code}",
                category, cultureCode);
            return category.ToString();
        }
    }

    /// <summary>
    /// 获取所有语义分类在当前语言下的本地化文本映射。
    /// 用于 UI 批量渲染（如分组树节点生成）。
    /// </summary>
    /// <returns>分类 → 本地化文本 的字典</returns>
    public IReadOnlyDictionary<SemanticCategory, string> GetAllLabels()
    {
        try
        {
            var isEn = IsEnglish(_currentLanguage);
            var result = new Dictionary<SemanticCategory, string>();

            foreach (var kv in LabelMap)
            {
                result[kv.Key] = isEn ? kv.Value.En : kv.Value.Zh;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量获取语义标签失败");
            return new Dictionary<SemanticCategory, string>();
        }
    }

    /// <summary>
    /// 获取所有语义分类及其在两种语言下的文本。
    /// 用于测试验证和导出报告。
    /// </summary>
    public static IReadOnlyDictionary<SemanticCategory, (string Zh, string En)> GetRawLabelMap()
    {
        return new Dictionary<SemanticCategory, (string Zh, string En)>(LabelMap);
    }

    // ================================================================
    // 私有方法
    // ================================================================

    /// <summary>
    /// 从中文标签文本反向查找 SemanticCategory 枚举值。
    /// 支持中文标签（如"系统临时文件"）和英文标签（如"System Temporary Files"）。
    /// </summary>
    private static bool TryParseCategory(string label, out SemanticCategory category)
    {
        foreach (var kv in LabelMap)
        {
            if (string.Equals(kv.Value.Zh, label, StringComparison.Ordinal)
                || string.Equals(kv.Value.En, label, StringComparison.Ordinal))
            {
                category = kv.Key;
                return true;
            }
        }

        // 尝试按枚举名称匹配（容错）
        if (Enum.TryParse<SemanticCategory>(label, ignoreCase: true, out var parsed))
        {
            category = parsed;
            return true;
        }

        category = default;
        return false;
    }

    /// <summary>
    /// 判断语言代码是否代表英语。
    /// </summary>
    private static bool IsEnglish(string cultureCode)
    {
        return cultureCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 语言切换事件处理器。
    /// </summary>
    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        _currentLanguage = e.NewLanguage;
        _logger.LogDebug("语义标签本地化器语言已同步: {Lang}", _currentLanguage);
    }

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _logger.LogDebug("SemanticLabelLocalizer 已释放");
    }
}
