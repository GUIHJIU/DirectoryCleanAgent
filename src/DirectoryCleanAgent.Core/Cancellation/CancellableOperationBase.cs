namespace DirectoryCleanAgent.Core.Cancellation;

/// <summary>
/// 可取消长耗时操作的抽象基类。
/// 封装 CancellationTokenSource 的创建、链接、超时设置和资源释放。
/// 派生类通过调用基类方法绑定取消行为，无需自行管理 CTS 生命周期。
/// </summary>
public abstract class CancellableOperationBase : IDisposable
{
    protected readonly ILogger Logger;
    private CancellationTokenSource? _cts;

    /// <summary>默认超时时间：120 秒（与设计文档 Everything 索引超时阈值一致）</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    protected CancellableOperationBase(ILogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>获取当前操作的取消令牌</summary>
    protected CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;

    /// <summary>当前操作是否已被取消</summary>
    protected bool IsCancellationRequested =>
        _cts?.IsCancellationRequested ?? false;

    /// <summary>
    /// 创建并绑定 CancellationTokenSource。
    /// 支持与外部传入的 Token 链接（如 UI 取消按钮），并可选自定义超时。
    /// </summary>
    /// <param name="externalToken">外部取消令牌（如来自 UI 的 Cancel 按钮）</param>
    /// <param name="timeout">超时时间，null 使用默认 120 秒</param>
    protected void CreateCancellationTokenSource(
        CancellationToken externalToken = default,
        TimeSpan? timeout = null)
    {
        // 释放上一次操作的 CTS（如果未释放）
        CancelAndDisposeCts();

        var effectiveTimeout = timeout ?? DefaultTimeout;

        if (externalToken != CancellationToken.None)
        {
            // 链接外部令牌后设置超时：避免先创建 CTS 再被覆盖的浪费
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _cts.CancelAfter(effectiveTimeout);
        }
        else
        {
            _cts = new CancellationTokenSource(effectiveTimeout);
        }

        Logger.LogDebug("CancellationTokenSource 已创建，超时={Timeout}s，链接外部Token={HasExternal}",
            effectiveTimeout.TotalSeconds, externalToken != CancellationToken.None);
    }

    /// <summary>
    /// 检查取消状态，若已请求取消则抛出 OperationCanceledException。
    /// 在长耗时循环的每次迭代中调用，实现快速响应取消请求。
    /// </summary>
    protected void ThrowIfCancellationRequested()
    {
        if (_cts != null)
        {
            _cts.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>取消当前操作并释放 CTS 资源</summary>
    protected void CancelAndDisposeCts()
    {
        if (_cts != null)
        {
            try
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
            }
            catch (ObjectDisposedException) { /* 已释放，忽略 */ }
            catch (AggregateException) { /* 链接的 CTS 可能已取消 */ }

            _cts.Dispose();
            _cts = null;
            Logger.LogDebug("CancellationTokenSource 已取消并释放");
        }
    }

    /// <summary>
    /// 安全执行可取消的异步操作，统一处理取消异常和错误日志。
    /// </summary>
    /// <param name="operation">异步操作委托</param>
    /// <param name="operationName">操作名称（用于日志）</param>
    /// <param name="externalToken">外部取消令牌</param>
    /// <param name="timeout">超时时间</param>
    protected async Task ExecuteCancellableAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken externalToken = default,
        TimeSpan? timeout = null)
    {
        try
        {
            Logger.LogInformation("[{Op}] 操作开始", operationName);
            CreateCancellationTokenSource(externalToken, timeout);
            await operation(_cts!.Token).ConfigureAwait(false);
            Logger.LogInformation("[{Op}] 操作完成", operationName);
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning("[{Op}] 操作被取消", operationName);
            // 统一抛出 OperationCanceledException：Task.Delay 取消时抛出 TaskCanceledException
            // （继承自 OperationCanceledException），重新包装确保调用方收到标准异常类型
            throw new OperationCanceledException("操作被取消", ex, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Op}] 操作失败", operationName);
            throw;
        }
        finally
        {
            CancelAndDisposeCts();
        }
    }

    public void Dispose()
    {
        CancelAndDisposeCts();
    }
}
