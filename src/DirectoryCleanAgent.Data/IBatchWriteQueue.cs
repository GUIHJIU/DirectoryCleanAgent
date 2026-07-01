namespace DirectoryCleanAgent.Data;

/// <summary>
/// 批量写入队列接口。
///
/// 所有 SQLite 写操作不直接执行，而是先进入内存 ConcurrentQueue。
/// 后台任务每 500ms 或队列累积达到 200 条时，执行一次批量写入事务。
/// 此机制彻底消除写锁竞争与刷写风暴（设计文档 5.2 节）。
///
/// 生命周期：通过 IDisposable 管理，释放时自动完成最后一次 Flush。
/// </summary>
public interface IBatchWriteQueue<T> : IDisposable
{
    /// <summary>将单条记录加入写入队列（非阻塞，调用方立即返回）</summary>
    void Enqueue(T item);

    /// <summary>将多条记录批量加入写入队列</summary>
    void EnqueueBatch(IEnumerable<T> items);

    /// <summary>
    /// 强制立即刷新队列中的所有待写入项。
    /// 常用于应用退出前、操作完成后确保数据落盘。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>当前队列中待写入的项数（用于监控）</summary>
    int PendingCount { get; }

    /// <summary>队列是否已释放</summary>
    bool IsDisposed { get; }
}
