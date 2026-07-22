namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything SDK 全局状态的共享锁。
///
/// SDK 2.0 使用进程全局状态模型，所有搜索参数和结果缓冲区是全局共享的。
/// 此类封装 SemaphoreSlim(1,1) 确保同一时刻只有一个操作访问 SDK 全局状态，
/// 避免并发查询导致数据竞争。
///
/// 注入方式：DI Singleton，生命周期与应用程序一致。
/// </summary>
public sealed class EverythingSdkLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    /// <summary>
    /// 初始化共享锁，初始容量为 1（互斥访问）。
    /// </summary>
    public EverythingSdkLock()
    {
        _semaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// 异步等待获取锁。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>成功获取锁时返回 true；被取消时抛出 OperationCanceledException</returns>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步等待获取锁。
    /// </summary>
    /// <param name="millisecondsTimeout">等待超时时间（毫秒）</param>
    /// <returns>成功获取锁返回 true；超时返回 false</returns>
    public bool Wait(int millisecondsTimeout)
    {
        ThrowIfDisposed();
        return _semaphore.Wait(millisecondsTimeout);
    }

    /// <summary>
    /// 释放锁，允许下一个等待者进入。
    /// </summary>
    public void Release()
    {
        ThrowIfDisposed();
        _semaphore.Release();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _semaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EverythingSdkLock));
    }
}
