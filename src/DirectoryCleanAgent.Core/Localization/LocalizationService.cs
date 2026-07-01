using System.Globalization;
using System.Resources;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.Core.Localization;

/// <summary>
/// 本地化服务的默认实现。
/// 使用 ResourceManager 从嵌入的 .resx 资源文件中检索本地化字符串。
/// 支持运行时语言切换，无需重启应用。
///
/// 资源回退链：请求语言 → 父语言（如 en-US → en） → 默认语言（zh-CN）
/// 若所有语言中均无对应键，返回 "[[key]]" 占位符以辅助开发排查。
/// </summary>
public class LocalizationService : ILocalizationService, IDisposable
{
    private readonly ILogger<LocalizationService> _logger;
    private readonly IConfigService _config;
    private readonly ResourceManager _resourceManager;

    // 语言代码 → CultureInfo 的缓存，避免重复创建
    private readonly Dictionary<string, CultureInfo> _cultureCache = new(StringComparer.OrdinalIgnoreCase);

    // 支持的语言列表
    private readonly List<string> _supportedLanguages = new() { "zh-CN", "en-US" };

    private string _currentLanguage;
    private readonly object _lock = new();

    /// <summary>
    /// 创建本地化服务实例。
    /// 初始语言从 IConfigService.Current.Language 读取。
    /// </summary>
    /// <param name="logger">结构化日志记录器</param>
    /// <param name="config">配置服务（用于获取初始语言设置）</param>
    /// <exception cref="ArgumentNullException">任一参数为 null</exception>
    public LocalizationService(ILogger<LocalizationService> logger, IConfigService config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        try
        {
            // ResourceManager baseName 必须与 .resx 文件的完全限定名一致
            // 文件位于 Localization/Strings.resx，默认命名空间为 DirectoryCleanAgent.Core
            _resourceManager = new ResourceManager(
                "DirectoryCleanAgent.Core.Localization.Strings",
                typeof(LocalizationService).Assembly);

            // 从用户配置中读取当前语言，若无效则回退到默认语言
            var configLanguage = _config.Current.Language;
            _currentLanguage = IsSupportedLanguage(configLanguage) ? configLanguage : "zh-CN";

            _logger.LogInformation(
                "本地化服务初始化完成。当前语言={Language}, 支持语言=[{Supported}]",
                _currentLanguage, string.Join(", ", _supportedLanguages));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "本地化服务初始化失败，将使用仅占位符的降级模式");
            // 不在构造函数中抛异常，允许应用以降级模式启动
            _currentLanguage = "zh-CN";
            _resourceManager = null!;
        }

        // 预热文化缓存
        foreach (var lang in _supportedLanguages)
        {
            try
            {
                _cultureCache[lang] = new CultureInfo(lang);
            }
            catch (CultureNotFoundException ex)
            {
                _logger.LogWarning(ex, "不支持的区域性代码: {Code}", lang);
            }
        }

        // 订阅配置变更，同步语言设置
        _config.ConfigChanged += OnConfigChanged;
    }

    // ================================================================
    // 公开属性与事件
    // ================================================================

    /// <inheritdoc />
    public string CurrentLanguage
    {
        get
        {
            lock (_lock)
            {
                return _currentLanguage;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => _supportedLanguages.AsReadOnly();

    // ================================================================
    // 公开方法
    // ================================================================

    /// <inheritdoc />
    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            _logger.LogWarning("GetString 收到空键");
            return string.Empty;
        }

        try
        {
            var culture = GetCultureForLanguage(_currentLanguage);
            if (_resourceManager == null)
            {
                return $"[[{key}]]";
            }

            // 获取指定语言的本地化字符串
            var value = _resourceManager.GetString(key, culture);

            // 若指定语言中无此键，回退到默认语言（zh-CN）
            if (string.IsNullOrEmpty(value))
            {
                _logger.LogDebug(
                    "键 '{Key}' 在语言 {Lang} 中缺失，尝试回退到 zh-CN", key, _currentLanguage);
                var fallbackCulture = GetCultureForLanguage("zh-CN");
                value = _resourceManager.GetString(key, fallbackCulture);
            }

            // 若仍为空，返回占位符以便开发排查
            if (string.IsNullOrEmpty(value))
            {
                _logger.LogWarning("键 '{Key}' 在所有语言中均未找到，返回占位符", key);
                return $"[[{key}]]";
            }

            return value;
        }
        catch (MissingManifestResourceException ex)
        {
            // 嵌入资源文件缺失（可能构建配置问题）
            _logger.LogError(ex, "嵌入资源文件缺失，键: {Key}", key);
            return $"[[{key}]]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取本地化字符串失败: Key={Key}, Language={Language}",
                key, _currentLanguage);
            return $"[[{key}]]";
        }
    }

    /// <inheritdoc />
    public string GetString(string key, string cultureCode)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            if (!IsSupportedLanguage(cultureCode))
            {
                _logger.LogWarning("请求了不支持的语言代码: {Code}，回退到默认语言", cultureCode);
                cultureCode = "zh-CN";
            }

            var culture = GetCultureForLanguage(cultureCode);

            if (_resourceManager == null)
            {
                return $"[[{key}]]";
            }

            var value = _resourceManager.GetString(key, culture);

            if (string.IsNullOrEmpty(value))
            {
                var fallbackCulture = GetCultureForLanguage("zh-CN");
                value = _resourceManager.GetString(key, fallbackCulture);
            }

            return value ?? $"[[{key}]]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取指定语言字符串失败: Key={Key}, Culture={Code}", key, cultureCode);
            return $"[[{key}]]";
        }
    }

    /// <inheritdoc />
    public void SetLanguage(string cultureCode)
    {
        if (string.IsNullOrEmpty(cultureCode))
        {
            _logger.LogWarning("SetLanguage 收到空 cultureCode");
            return;
        }

        if (!IsSupportedLanguage(cultureCode))
        {
            _logger.LogWarning("尝试切换到不支持的语言: {Code}，已忽略", cultureCode);
            return;
        }

        string oldLanguage;
        string newLanguage;

        lock (_lock)
        {
            oldLanguage = _currentLanguage;
            if (string.Equals(oldLanguage, cultureCode, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("语言已是 {Code}，无需切换", cultureCode);
                return;
            }
            _currentLanguage = cultureCode;
            newLanguage = _currentLanguage;
        }

        _logger.LogInformation("语言已切换: {Old} → {New}", oldLanguage, newLanguage);

        // 异步持久化到用户配置（不阻塞 UI）
        _ = Task.Run(async () =>
        {
            try
            {
                // 注意：这里通过 IConfigService 修改配置后保存
                // _config.Current.Language 是可变引用，直接修改后调用 SaveAsync
                _config.Current.Language = cultureCode;
                await _config.SaveAsync().ConfigureAwait(false);
                _logger.LogDebug("语言设置已持久化: {Lang}", cultureCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "持久化语言设置失败");
            }
        });

        // 触发 UI 层刷新
        OnLanguageChanged(oldLanguage, newLanguage);
    }

    // ================================================================
    // 私有方法
    // ================================================================

    /// <summary>
    /// 检查给定的语言代码是否在支持列表中。
    /// </summary>
    private bool IsSupportedLanguage(string cultureCode)
    {
        return _supportedLanguages.Contains(cultureCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取语言代码对应的 CultureInfo。
    /// 使用缓存避免重复创建 CultureInfo 实例。
    /// </summary>
    private CultureInfo GetCultureForLanguage(string languageCode)
    {
        if (_cultureCache.TryGetValue(languageCode, out var cached))
        {
            return cached;
        }

        try
        {
            var culture = new CultureInfo(languageCode);
            _cultureCache[languageCode] = culture;
            return culture;
        }
        catch (CultureNotFoundException)
        {
            _logger.LogWarning("无法创建 CultureInfo: {Code}，回退到 zh-CN", languageCode);
            return _cultureCache["zh-CN"];
        }
    }

    /// <summary>
    /// 配置变更回调：若语言设置被外部修改，同步当前语言。
    /// </summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        try
        {
            var configLang = _config.Current.Language;
            if (!string.Equals(_currentLanguage, configLang, StringComparison.OrdinalIgnoreCase)
                && IsSupportedLanguage(configLang))
            {
                var old = _currentLanguage;
                lock (_lock)
                {
                    _currentLanguage = configLang;
                }
                _logger.LogInformation(
                    "配置变更导致语言切换: {Old} → {New}", old, configLang);
                OnLanguageChanged(old, configLang);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理配置变更时出错");
        }
    }

    /// <summary>
    /// 安全触发 LanguageChanged 事件。
    /// 每个处理器在独立的 try-catch 中执行，防止单个订阅者异常影响其他订阅者。
    /// </summary>
    private void OnLanguageChanged(string oldLanguage, string newLanguage)
    {
        var handlers = LanguageChanged;
        if (handlers == null) return;

        var args = new LanguageChangedEventArgs(oldLanguage, newLanguage);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<LanguageChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LanguageChanged 事件处理器抛出异常: {Handler}",
                    handler.Method.Name);
            }
        }
    }

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        _cultureCache.Clear();
        _logger.LogDebug("LocalizationService 已释放");
    }
}
