namespace DirectoryCleanAgent.Core.Localization;

/// <summary>
/// 语言切换事件参数，携带切换前后的语言代码。
/// UI 层可据此决定是否需要全量刷新或仅更新特定控件。
/// </summary>
public class LanguageChangedEventArgs : EventArgs
{
    /// <summary>切换前的语言代码（如 "zh-CN"）</summary>
    public string OldLanguage { get; }

    /// <summary>切换后的语言代码（如 "en-US"）</summary>
    public string NewLanguage { get; }

    /// <summary>
    /// 初始化语言变更事件参数。
    /// </summary>
    /// <param name="oldLanguage">原语言代码</param>
    /// <param name="newLanguage">新语言代码</param>
    public LanguageChangedEventArgs(string oldLanguage, string newLanguage)
    {
        OldLanguage = oldLanguage ?? throw new ArgumentNullException(nameof(oldLanguage));
        NewLanguage = newLanguage ?? throw new ArgumentNullException(nameof(newLanguage));
    }
}
