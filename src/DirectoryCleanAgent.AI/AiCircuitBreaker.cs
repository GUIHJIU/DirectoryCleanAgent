using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// 熔断器状态机，保护 AI API 不被连续失败请求打垮。
///
/// 状态转换规则：
///   Closed（正常） → 连续 3 次失败 → Open（熔断）
///   Open（熔断）   → 30 秒超时      → HalfOpen（半开，允许探针请求）
///   HalfOpen（半开）→ 探针成功       → Closed（恢复）
///   HalfOpen（半开）→ 探针失败       → Open（重新熔断）
///
/// 线程安全：所有状态变更通过 lock 保护。
/// </summary>
internal sealed class AiCircuitBreaker
{
    /// <summary>熔断器当前状态</summary>
    public enum BreakerState
    {
        /// <summary>闭合：正常处理请求</summary>
        Closed,

        /// <summary>打开：拒绝所有请求，快速失败</summary>
        Open,

        /// <summary>半开：允许 1 个探针请求通过，成功则恢复，失败则重新熔断</summary>
        HalfOpen
    }

    private const int FailureThreshold = 3;              // 连续失败 3 次后熔断
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30); // 熔断 30 秒后尝试恢复

    private readonly ILogger<AiCircuitBreaker> _logger;
    private readonly object _lock = new();

    private BreakerState _state = BreakerState.Closed;
    private int _consecutiveFailures;
    private DateTime _openStartedAt = DateTime.MinValue;

    public AiCircuitBreaker(ILogger<AiCircuitBreaker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前熔断器状态（线程安全）</summary>
    public BreakerState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 在每次 API 调用前调用。
    /// 若熔断器处于 Open 状态则抛出 CircuitBreakerOpenException。
    /// 处于 HalfOpen 状态时允许探针请求通过。
    /// </summary>
    /// <exception cref="CircuitBreakerOpenException">熔断器打开时抛出</exception>
    public void EnsureNotOpen()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case BreakerState.Closed:
                    // 正常放行
                    return;

                case BreakerState.HalfOpen:
                    // 允许探针请求通过（调用方负责处理结果并调用 RecordSuccess/RecordFailure）
                    _logger.LogInformation("熔断器 HalfOpen：允许探针请求通过");
                    return;

                case BreakerState.Open:
                    // 检查是否已过超时时间
                    var elapsed = DateTime.UtcNow - _openStartedAt;
                    if (elapsed >= OpenTimeout)
                    {
                        // 超时到期 → 进入 HalfOpen
                        _state = BreakerState.HalfOpen;
                        _logger.LogInformation("熔断器 Open→HalfOpen（已过 {Elapsed:F1} 秒），探针请求放行", elapsed.TotalSeconds);
                        return;
                    }

                    // 仍在熔断期间 → 拒绝
                    var remaining = OpenTimeout - elapsed;
                    _logger.LogWarning("熔断器 Open：拒绝请求（剩余 {Remaining:F1} 秒恢复）", remaining.TotalSeconds);
                    throw new CircuitBreakerOpenException(
                        $"AI API 熔断器已打开，{remaining.TotalSeconds:F0} 秒后自动恢复。连续 {_consecutiveFailures} 次调用失败触发了熔断保护。");
            }
        }
    }

    /// <summary>
    /// API 调用成功后调用。重置失败计数。
    /// 若当前为 HalfOpen 状态则转换到 Closed。
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            var previousState = _state;

            _consecutiveFailures = 0;

            if (_state == BreakerState.HalfOpen)
            {
                _state = BreakerState.Closed;
                _logger.LogInformation("熔断器 HalfOpen→Closed：探针请求成功，熔断器恢复");
            }
            else if (_state == BreakerState.Open)
            {
                // 理论上不应发生（Open 状态下不会有成功的请求），但防御处理
                _state = BreakerState.Closed;
                _logger.LogWarning("熔断器 Open→Closed：异常状态恢复（Open 状态下收到了成功记录）");
            }
        }
    }

    /// <summary>
    /// API 调用失败后调用。递增失败计数。
    /// 达到阈值时切换到 Open；若当前为 HalfOpen 则重新熔断。
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_state == BreakerState.HalfOpen)
            {
                // 半开探针失败 → 立即重新熔断
                _state = BreakerState.Open;
                _openStartedAt = DateTime.UtcNow;
                _logger.LogWarning("熔断器 HalfOpen→Open：探针请求失败，重新熔断（连续失败={Count}）", _consecutiveFailures);
            }
            else if (_consecutiveFailures >= FailureThreshold && _state == BreakerState.Closed)
            {
                // 连续失败达到阈值 → 熔断
                _state = BreakerState.Open;
                _openStartedAt = DateTime.UtcNow;
                _logger.LogError("熔断器 Closed→Open：连续 {Count} 次失败触发熔断保护，{Timeout} 秒后尝试恢复",
                    _consecutiveFailures, OpenTimeout.TotalSeconds);
            }
            else
            {
                _logger.LogDebug("熔断器累计失败: {Count}/{Threshold}（状态={State}）",
                    _consecutiveFailures, FailureThreshold, _state);
            }
        }
    }
}

/// <summary>
/// 熔断器打开时抛出的异常。调用方应捕获此异常并停止所有请求。
/// </summary>
public sealed class CircuitBreakerOpenException : InvalidOperationException
{
    public CircuitBreakerOpenException(string message) : base(message) { }

    public CircuitBreakerOpenException(string message, Exception inner) : base(message, inner) { }
}
