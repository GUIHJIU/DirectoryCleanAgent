namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 后台等待完成事件参数 — 携带索引等待结果（成功/超时）。
/// 由 SetupWizardViewModel 在后台等待完成后通过 BackgroundWaitReady 事件
/// 传递给 App 层，App 层据此决定显示何种通知及后续行为。
/// </summary>
public class BackgroundWaitReadyEventArgs : EventArgs
{
    /// <summary>索引是否已就绪</summary>
    public bool IsIndexReady { get; }

    /// <summary>索引等待是否超时（120秒无响应）</summary>
    public bool IsTimeout { get; }

    private BackgroundWaitReadyEventArgs(bool isIndexReady, bool isTimeout)
    {
        IsIndexReady = isIndexReady;
        IsTimeout = isTimeout;
    }

    /// <summary>索引就绪（成功）</summary>
    public static BackgroundWaitReadyEventArgs Success() => new(isIndexReady: true, isTimeout: false);

    /// <summary>索引等待超时（降级为警告模式）</summary>
    public static BackgroundWaitReadyEventArgs Timeout() => new(isIndexReady: false, isTimeout: true);
}
