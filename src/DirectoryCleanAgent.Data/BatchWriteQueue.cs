using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 通用批量写入队列实现。
///
/// 将所有 SQLite 写操作先收集到内存 ConcurrentQueue 中，
/// 后台任务每 500ms 或队列累积达到 200 条时自动触发批量刷新。
/// 批量刷新在单事务中执行，失败时自动回滚并将失败条目退回队列。
///
/// 设计意图（设计文档 5.2 节）：
/// - 消除高频小写入导致的 SQLite 写锁竞争
/// - 避免 WAL 模式下写入过多导致的 WAL 文件膨胀
/// - 将随机写合并为顺序批量写，显著提升写入吞吐量
/// </summary>
/// <typeparam name="T">队列中存储的条目类型</typeparam>
public sealed class BatchWriteQueue<T> : IBatchWriteQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flushHandler;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger _logger;
    private readonly string _name;

    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundTask;
    private volatile int _pendingCount;

    // 后台轮询间隔：在 500ms 刷新周期内以 50ms 粒度检查取消
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 创建批量写入队列。
    /// </summary>
    /// <param name="flushHandler">批量刷新回调，接收条目列表和取消令牌</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="name">队列名称（用于日志区分）</param>
    /// <param name="batchSize">触发写入的条目数阈值（默认 200）</param>
    /// <param name="flushIntervalMs">定时刷新的毫秒间隔（默认 500ms）</param>
    public BatchWriteQueue(
        Func<IReadOnlyList<T>, CancellationToken, Task> flushHandler,
        ILogger logger,
        string name = "default",
        int batchSize = 200,
        int flushIntervalMs = 500)
    {
        _flushHandler = flushHandler ?? throw new ArgumentNullException(nameof(flushHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _name = name;
        _batchSize = batchSize > 0 ? batchSize : 200;
        _flushInterval = TimeSpan.FromMilliseconds(flushIntervalMs > 0 ? flushIntervalMs : 500);

        // 启动后台轮询任务
        _backgroundTask = Task.Run(() => RunBackgroundLoopAsync(_cts.Token));

        _logger.LogDebug("[批量队列:{Name}] 已创建，批量大小={BatchSize}，刷新间隔={Interval}ms",
            _name, _batchSize, _flushInterval.TotalMilliseconds);
    }

    /// <inheritdoc/>
    public int PendingCount => _pendingCount;

    /// <inheritdoc/>
    public bool IsDisposed => _cts.IsCancellationRequested;

    /// <inheritdoc/>
    public void Enqueue(T item)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(BatchWriteQueue<T>));

        _queue.Enqueue(item);
        Interlocked.Increment(ref _pendingCount);

        // 达到批量阈值时，触发一次非阻塞刷新
        if (_pendingCount >= _batchSize)
        {
            _ = Task.Run(() => FlushIfNotLockedAsync(_cts.Token));
        }
    }

    /// <inheritdoc/>
    public void EnqueueBatch(IEnumerable<T> items)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(BatchWriteQueue<T>));

        var count = 0;
        foreach (var item in items)
        {
            _queue.Enqueue(item);
            count++;
        }

        Interlocked.Add(ref _pendingCount, count);

        if (_pendingCount >= _batchSize)
        {
            _ = Task.Run(() => FlushIfNotLockedAsync(_cts.Token));
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (IsDisposed && _queue.IsEmpty)
            return;

        await FlushCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>后台轮询循环：每 500ms 检查一次是否需要刷新</summary>
    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 等待轮询间隔，但要响应取消
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                // 检查是否已到达 500ms 刷新周期
                if (stopwatch.Elapsed >= _flushInterval)
                {
                    stopwatch.Restart();

                    // 只在有数据且未在刷新时触发
                    if (_pendingCount > 0)
                    {
                        await FlushIfNotLockedAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break; // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[批量队列:{Name}] 后台轮询异常", _name);
            }
        }
    }

    /// <summary>非阻塞刷新：仅在未锁定时执行，避免多线程同时刷新</summary>
    private async Task FlushIfNotLockedAsync(CancellationToken ct)
    {
        // 若锁已被占用（另一线程正在刷新），直接返回
        if (!await _flushLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            await FlushCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>核心刷新逻辑：从队列中取出所有条目并交给 flushHandler 批量写入</summary>
    private async Task FlushCoreAsync(CancellationToken ct)
    {
        // 从队列中取出所有待写入条目
        var batch = new List<T>(Math.Min(_pendingCount, _batchSize * 2));
        while (_queue.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count == 0)
            return;

        Interlocked.Add(ref _pendingCount, -batch.Count);

        try
        {
            _logger.LogDebug("[批量队列:{Name}] 开始刷新 {Count} 条记录", _name, batch.Count);
            await _flushHandler(batch.AsReadOnly(), ct).ConfigureAwait(false);
            _logger.LogDebug("[批量队列:{Name}] 成功刷新 {Count} 条记录", _name, batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 写入失败：将条目退回队列，下次重试
            _logger.LogError(ex, "[批量队列:{Name}] 批量写入失败，将 {Count} 条记录退回队列",
                _name, batch.Count);

            foreach (var item in batch)
            {
                _queue.Enqueue(item);
            }
            Interlocked.Add(ref _pendingCount, batch.Count);
        }
    }

    /// <summary>
    /// 释放队列资源。
    /// 等待后台任务退出，并执行最后一次强制刷新以确保数据不丢失。
    /// </summary>
    public void Dispose()
    {
        if (_cts.IsCancellationRequested)
            return;

        _logger.LogDebug("[批量队列:{Name}] 正在释放...", _name);

        // 通知后台任务停止
        _cts.Cancel();

        // 执行最后一次刷新
        try
        {
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[批量队列:{Name}] 最终刷新失败", _name);
        }

        _backgroundTask.Wait(TimeSpan.FromSeconds(5));

        _flushLock.Dispose();
        _cts.Dispose();

        _logger.LogDebug("[批量队列:{Name}] 已释放", _name);
    }
}
