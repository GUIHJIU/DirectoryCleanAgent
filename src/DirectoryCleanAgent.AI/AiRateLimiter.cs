using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// 令牌桶限流器：全局 30 RPM + 最大并发 5。
///
/// 工作原理：
///   令牌桶容量 = 30（对应 30 RPM）
///   补充速率 = 0.5 令牌/秒（30/60 = 0.5）
///   并发上限 = SemaphoreSlim(5, 5)
///
/// 每个 API 调用前必须调用 TryAcquireAsync：
///   1. 先等待 SemaphoreSlim 获取并发槽位
///   2. 再自旋等待令牌可用（100ms 间隔，支持取消）
///   3. 获取成功后消耗 1 个令牌
///   4. 调用完成后必须在 finally 中调用 Release() 释放并发槽位
///
/// 线程安全：lock 保护令牌计数，SemaphoreSlim 控制并发。
/// </summary>
internal sealed class AiRateLimiter : IDisposable
{
    private const int MaxTokens = 30;            // 最大积攒令牌数
    private const double TokensPerSecond = 0.5;  // 每秒补充 0.5 个 → 30/分钟
    private const int MaxConcurrency = 5;        // 最大并发数

    private readonly SemaphoreSlim _concurrencySemaphore = new(MaxConcurrency, MaxConcurrency);
    private readonly ILogger<AiRateLimiter> _logger;

    private double _currentTokens = MaxTokens;
    private DateTime _lastReplenishTime = DateTime.UtcNow;
    private readonly object _lock = new();
    private bool _disposed;

    public AiRateLimiter(ILogger<AiRateLimiter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 尝试获取令牌和并发槽位。
    /// 调用方必须在 finally 块中调用 Release() 释放并发槽位。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 表示成功获取，false 表示被取消</returns>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        // 步骤1：等待并发槽位（最多 5 个并发请求）
        await _concurrencySemaphore.WaitAsync(ct);

        try
        {
            // 步骤2：等待令牌可用（自旋等待，100ms 间隔）
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                lock (_lock)
                {
                    ReplenishTokens(); // 基于时间差补充令牌

                    if (_currentTokens >= 1.0)
                    {
                        _currentTokens -= 1.0;
                        return true; // 获取成功，并发槽位由调用方 Release() 释放
                    }
                }

                // 令牌不足，等待 100ms 后重试
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 被取消时必须释放并发槽位，防止槽位泄漏
            _concurrencySemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// 释放并发槽位。必须与 TryAcquireAsync 成对调用。
    /// </summary>
    public void Release()
    {
        try
        {
            _concurrencySemaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // 防御：多次 Release 不应导致崩溃
            _logger.LogWarning("AiRateLimiter.Release 在 Semaphore 已满时调用——可能存在未配对的 TryAcquire/Release");
        }
    }

    /// <summary>
    /// 根据时间差补充令牌（需在 lock 内调用）。
    /// 补充数量 = 经过秒数 × 0.5，最多积攒到 MaxTokens。
    /// </summary>
    private void ReplenishTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastReplenishTime).TotalSeconds;

        if (elapsed <= 0)
            return;

        // 补充令牌 = 经过秒数 × 每秒速率
        var tokensToAdd = elapsed * TokensPerSecond;
        _currentTokens = Math.Min(MaxTokens, _currentTokens + tokensToAdd);
        _lastReplenishTime = now;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _concurrencySemaphore.Dispose();
    }
}
