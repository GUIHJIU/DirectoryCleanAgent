namespace DirectoryCleanAgent.Core.Progress;

/// <summary>
/// 进度报告器接口。
/// 提供强类型的进度报告能力，比 IProgress&lt;T&gt; 增加完成通知和错误通知。
/// </summary>
/// <typeparam name="T">进度数据类型</typeparam>
public interface IProgressReporter<in T>
{
    /// <summary>报告当前进度</summary>
    void Report(T value);

    /// <summary>通知进度完成</summary>
    void ReportComplete();

    /// <summary>通知进度过程中发生错误</summary>
    void ReportError(Exception ex);
}

/// <summary>
/// IProgressReporter 的默认实现，委托给 IProgress&lt;ProgressReport&gt;。
/// 封装 .NET 标准 IProgress 机制，同时提供完成和错误通知能力。
/// </summary>
public class DefaultProgressReporter : IProgressReporter<ProgressReport>
{
    private readonly IProgress<ProgressReport>? _progress;
    private readonly ILogger<DefaultProgressReporter>? _logger;

    public DefaultProgressReporter(IProgress<ProgressReport>? progress = null,
        ILogger<DefaultProgressReporter>? logger = null)
    {
        _progress = progress;
        _logger = logger;
    }

    public void Report(ProgressReport value)
    {
        try
        {
            _progress?.Report(value);
            _logger?.LogDebug("进度: {Progress}", value.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "进度报告回调抛出异常（不中断操作）");
        }
    }

    public void ReportComplete()
    {
        _logger?.LogInformation("进度报告：操作完成");
        // 最终进度报告由调用方在完成时自行发送
    }

    public void ReportError(Exception ex)
    {
        _logger?.LogError(ex, "进度报告：操作失败");
    }
}

/// <summary>
/// 组合多个 IProgress&lt;T&gt; 报告器，将进度同时分发到所有子报告器。
/// 典型场景：同时更新进度条、状态栏文本、日志记录。
/// </summary>
public class CompositeProgressReporter<T> : IProgress<T>
{
    private readonly IReadOnlyList<IProgress<T>> _reporters;

    public CompositeProgressReporter(params IProgress<T>[] reporters)
    {
        _reporters = reporters ?? throw new ArgumentNullException(nameof(reporters));
    }

    public CompositeProgressReporter(IEnumerable<IProgress<T>> reporters)
    {
        _reporters = reporters?.ToList() ?? throw new ArgumentNullException(nameof(reporters));
    }

    public void Report(T value)
    {
        foreach (var reporter in _reporters)
        {
            try
            {
                reporter.Report(value);
            }
            catch
            {
                // 一个报告器失败不影响其他报告器
            }
        }
    }
}
