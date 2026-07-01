using System.Reflection;
using DirectoryCleanAgent.AI;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AiCircuitBreaker 单元测试 —— 覆盖状态机转换、线程安全、异常处理。
/// 通过反射操纵 _openStartedAt 字段来模拟时间流逝，避免修改 static readonly OpenTimeout。
/// </summary>
public class AiCircuitBreakerTests
{
    private readonly ILogger<AiCircuitBreaker> _logger;

    public AiCircuitBreakerTests()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<AiCircuitBreaker>();
    }

    // ============================================================
    // 构造函数与初始状态
    // ============================================================

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AiCircuitBreaker(null!));
    }

    [Fact]
    public void Constructor_ValidLogger_CreatesInstance()
    {
        var cb = new AiCircuitBreaker(_logger);
        Assert.NotNull(cb);
    }

    [Fact]
    public void State_Initial_ReturnsClosed()
    {
        var cb = new AiCircuitBreaker(_logger);
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
    }

    // ============================================================
    // EnsureNotOpen —— 各状态下的行为
    // ============================================================

    [Fact]
    public void EnsureNotOpen_WhenClosed_DoesNotThrow()
    {
        var cb = new AiCircuitBreaker(_logger);
        // Closed 状态下应直接放行，不抛异常
        var ex = Record.Exception(() => cb.EnsureNotOpen());
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureNotOpen_WhenHalfOpen_DoesNotThrow()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 先触发 3 次失败进入 Open
        TriggerOpen(cb);
        // 操纵 _openStartedAt 为过去时间以模拟超时过期
        SetOpenStartedAt(cb, DateTime.UtcNow.AddSeconds(-30));
        // 此时 EnsureNotOpen 应检测到超时过期 → 自动进入 HalfOpen → 放行
        var ex = Record.Exception(() => cb.EnsureNotOpen());
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureNotOpen_WhenOpenBeforeTimeout_ThrowsCircuitBreakerOpenException()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 触发 Open 状态（_openStartedAt 设为当前时间）
        TriggerOpen(cb);
        // 立即调用应抛出（超时未到）
        Assert.Throws<CircuitBreakerOpenException>(() => cb.EnsureNotOpen());
    }

    [Fact]
    public void EnsureNotOpen_WhenOpenAfterTimeout_TransitionsToHalfOpen()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 进入 Open 状态
        TriggerOpen(cb);
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);

        // 模拟超时已过期（将 _openStartedAt 设为 30 秒前）
        SetOpenStartedAt(cb, DateTime.UtcNow.AddSeconds(-30));
        // 调用 EnsureNotOpen 应自动转换到 HalfOpen 并放行
        cb.EnsureNotOpen();
        Assert.Equal(AiCircuitBreaker.BreakerState.HalfOpen, cb.State);
    }

    // ============================================================
    // RecordSuccess —— 各状态下的行为
    // ============================================================

    [Fact]
    public void RecordSuccess_WhenClosed_ResetsFailureCount()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 记录 2 次失败，不应触发熔断
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);

        // 成功应重置计数
        cb.RecordSuccess();
        // 再记录两次失败，不会触发熔断（因为计数已重置）
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
    }

    [Fact]
    public void RecordSuccess_WhenHalfOpen_TransitionsToClosed()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 先进入 Open → 模拟超时过期 → HalfOpen
        TriggerOpen(cb);
        SetOpenStartedAt(cb, DateTime.UtcNow.AddSeconds(-30));
        cb.EnsureNotOpen(); // 进入 HalfOpen
        Assert.Equal(AiCircuitBreaker.BreakerState.HalfOpen, cb.State);

        // 探针成功 → 恢复 Closed
        cb.RecordSuccess();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
    }

    [Fact]
    public void RecordSuccess_WhenOpen_DefensivelyTransitionsToClosed()
    {
        var cb = new AiCircuitBreaker(_logger);
        TriggerOpen(cb);
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);

        // 理论上不应发生（Open 状态不会有成功请求），但防御处理
        cb.RecordSuccess();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
    }

    // ============================================================
    // RecordFailure —— 状态转换核心逻辑
    // ============================================================

    [Fact]
    public void RecordFailure_FailureCountBelowThreshold_StaysClosed()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 第 1 次失败
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
        // 第 2 次失败（仍未达到阈值 3）
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Closed, cb.State);
    }

    [Fact]
    public void RecordFailure_ThirdFailure_TransitionsToOpen()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 连续 3 次失败触发 Closed→Open
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);
    }

    [Fact]
    public void RecordFailure_WhenHalfOpen_TransitionsToOpen()
    {
        var cb = new AiCircuitBreaker(_logger);
        // 先进入 Open → 模拟超时过期 → HalfOpen
        TriggerOpen(cb);
        SetOpenStartedAt(cb, DateTime.UtcNow.AddSeconds(-30));
        cb.EnsureNotOpen(); // HalfOpen

        // 探针失败 → 立即重新熔断
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);
    }

    [Fact]
    public void RecordFailure_WhenAlreadyOpen_StaysOpen()
    {
        var cb = new AiCircuitBreaker(_logger);
        TriggerOpen(cb);
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);

        // Open 状态下继续失败，保持 Open
        cb.RecordFailure();
        Assert.Equal(AiCircuitBreaker.BreakerState.Open, cb.State);
    }

    // ============================================================
    // 线程安全
    // ============================================================

    [Fact]
    public void State_ThreadSafety_ConcurrentAccess_NoDeadlock()
    {
        var cb = new AiCircuitBreaker(_logger);

        // 多线程并发读写状态
        Parallel.Invoke(
            () => { for (var i = 0; i < 100; i++) { cb.RecordFailure(); } },
            () => { for (var i = 0; i < 100; i++) { var _ = cb.State; } },
            () => { for (var i = 0; i < 100; i++) { try { cb.EnsureNotOpen(); } catch (CircuitBreakerOpenException) { } } },
            () => { for (var i = 0; i < 100; i++) { cb.RecordSuccess(); } }
        );

        // 无死锁即为通过
        Assert.True(true);
    }

    // ============================================================
    // 异常类测试
    // ============================================================

    [Fact]
    public void CircuitBreakerOpenException_Constructor_WithMessage()
    {
        var ex = new CircuitBreakerOpenException("熔断器已打开");
        Assert.Equal("熔断器已打开", ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void CircuitBreakerOpenException_Constructor_WithMessageAndInner()
    {
        var inner = new InvalidOperationException("内部异常");
        var ex = new CircuitBreakerOpenException("熔断器已打开", inner);
        Assert.Equal("熔断器已打开", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>触发三次连续失败，使熔断器进入 Open 状态</summary>
    private static void TriggerOpen(AiCircuitBreaker cb)
    {
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
    }

    /// <summary>通过反射设置 _openStartedAt 实例字段以模拟时间流逝</summary>
    private static void SetOpenStartedAt(AiCircuitBreaker cb, DateTime value)
    {
        var field = typeof(AiCircuitBreaker).GetField("_openStartedAt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(cb, value);
    }
}
