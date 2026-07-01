namespace DirectoryCleanAgent.Core.Cancellation;

/// <summary>
/// 取消令牌源提供器接口。
/// 提供创建和管理 CancellationTokenSource 的统一入口，
/// 支持超时取消和父级 Token 链接。
/// </summary>
public interface ICancellationTokenSourceProvider
{
    /// <summary>
    /// 创建一个新的 CancellationTokenSource。
    /// </summary>
    /// <param name="timeout">超时时间，null 表示不设置超时</param>
    /// <param name="linkedToken">需要链接的父级取消令牌</param>
    /// <returns>新的 CancellationTokenSource 实例</returns>
    CancellationTokenSource CreateLinkedSource(
        TimeSpan? timeout = null,
        CancellationToken linkedToken = default);

    /// <summary>
    /// 创建带默认超时（120 秒，与设计文档一致）的 CancellationTokenSource。
    /// </summary>
    CancellationTokenSource CreateWithDefaultTimeout(CancellationToken linkedToken = default);
}
