namespace DirectoryCleanAgent.Core.Localization;

/// <summary>
/// 本地化服务抽象接口。
/// 提供基于键的本地化字符串检索、运行时语言切换、以及语言变更通知。
/// 不依赖任何 UI 框架，可由 WPF/WinUI/命令行等任意宿主使用。
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// 获取当前语言下指定键的本地化字符串。
    /// 若当前语言中键不存在，自动回退到默认语言（zh-CN）。
    /// </summary>
    /// <param name="key">资源键（区分大小写）</param>
    /// <returns>本地化字符串；若键在所有语言中都不存在，返回 "[[key]]" 占位符</returns>
    string GetString(string key);

    /// <summary>
    /// 获取指定语言下指定键的本地化字符串。
    /// 若该语言中键不存在，回退到默认语言（zh-CN）。
    /// </summary>
    /// <param name="key">资源键</param>
    /// <param name="cultureCode">语言代码（"zh-CN" 或 "en-US"）</param>
    /// <returns>本地化字符串</returns>
    string GetString(string key, string cultureCode);

    /// <summary>
    /// 运行时切换当前语言并触发 LanguageChanged 事件。
    /// 若 cultureCode 不在支持列表中，操作被静默忽略并记录警告日志。
    /// </summary>
    /// <param name="cultureCode">目标语言代码</param>
    void SetLanguage(string cultureCode);

    /// <summary>当前语言代码（如 "zh-CN"）</summary>
    string CurrentLanguage { get; }

    /// <summary>语言切换事件。UI 层订阅后刷新所有文本绑定。</summary>
    event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    /// <summary>支持的语言代码列表（如 ["zh-CN", "en-US"]）</summary>
    IReadOnlyList<string> SupportedLanguages { get; }
}
