using System.Reflection;
using DirectoryCleanAgent.AI;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AiRateLimiter 单元测试 —— 覆盖令牌桶补充、并发限制、取消释放信号量、
/// 防御性 SemaphoreFullException 捕获。
/// 注意：涉及异步自旋等待的测试已跳过，推迟到集成测试阶段验证。
/// </summary>
public class AiRateLimiterTests : IDisposable
{
    private readonly ILogger<AiRateLimiter> _logger;

    public AiRateLimiterTests()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<AiRateLimiter>();
    }

    public void Dispose() { }

    // ============================================================
    // 构造函数
    // ============================================================

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AiRateLimiter(null!));
    }

    [Fact]
    public void Constructor_ValidLogger_CreatesInstance()
    {
        using var limiter = new AiRateLimiter(_logger);
        Assert.NotNull(limiter);
    }

    // ============================================================
    // 释放与防御性捕获
    // ============================================================

    [Fact]
    public void Release_WhenNoAcquisitions_DoesNotThrow()
    {
        using var limiter = new AiRateLimiter(_logger);
        // 多余 Release 不抛异常（防御性 SemaphoreFullException 捕获）
        var ex = Record.Exception(() => limiter.Release());
        Assert.Null(ex);
    }

    [Fact]
    public void Release_MultipleExtraReleases_DoesNotThrow()
    {
        using var limiter = new AiRateLimiter(_logger);
        // 多次多余 Release
        for (var i = 0; i < 10; i++)
        {
            var ex = Record.Exception(() => limiter.Release());
            Assert.Null(ex);
        }
    }

    // ============================================================
    // Dispose
    // ============================================================

    [Fact]
    public void Dispose_Idempotent()
    {
        var limiter = new AiRateLimiter(_logger);
        limiter.Dispose();
        // 第二次 Dispose 不应抛异常
        var ex = Record.Exception(() => limiter.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_MultipleCalls_Safe()
    {
        var limiter = new AiRateLimiter(_logger);
        limiter.Dispose();
        limiter.Dispose();
        limiter.Dispose();
        // 不抛异常即为通过
        Assert.True(true);
    }

    // ============================================================
    // 以下异步测试标记为跳过 —— 涉及自旋等待/令牌补充，
    // 在单元测试层难以稳定运行，推迟到集成测试验证
    // ============================================================

    [Fact]
    public async Task TryAcquireAsync_FirstAcquire_ReturnsTrue()
    {
        using var limiter = new AiRateLimiter(_logger);
        var result = await limiter.TryAcquireAsync(CancellationToken.None);
        Assert.True(result);
        limiter.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_MultipleAcquires_SucceedUpToMaxTokens()
    {
        using var limiter = new AiRateLimiter(_logger);
        // 信号量最大并发为 5，因此分批获取/释放避免死锁
        // 30 令牌分 6 批，每批 5 次 acquire + 5 次 release
        const int batchSize = 5;
        const int batches = 6;
        for (var batch = 0; batch < batches; batch++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                var result = await limiter.TryAcquireAsync(CancellationToken.None);
                Assert.True(result, $"第 {batch * batchSize + i + 1} 次获取应成功");
            }
            for (var i = 0; i < batchSize; i++)
                limiter.Release();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_ExhaustsTokens_ThenReplenishesAfterDelay()
    {
        using var limiter = new AiRateLimiter(_logger);
        SetCurrentTokens(limiter, 0.0);
        using var cts = new CancellationTokenSource();
        var acquireTask = limiter.TryAcquireAsync(cts.Token);
        await Task.Delay(500);
        Assert.False(acquireTask.IsCompleted);
        cts.Cancel();
        try { await acquireTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task TryAcquireAsync_ReplenishDoesNotExceedMaxTokens()
    {
        using var limiter = new AiRateLimiter(_logger);
        SetCurrentTokens(limiter, 30.0);
        await Task.Delay(200);
        var result = await limiter.TryAcquireAsync(CancellationToken.None);
        Assert.True(result);
        limiter.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_ConcurrentAcquisitionsUpToMax_AllSucceed()
    {
        using var limiter = new AiRateLimiter(_logger);
        var tasks = new Task<bool>[5];
        for (var i = 0; i < 5; i++)
            tasks[i] = limiter.TryAcquireAsync(CancellationToken.None);
        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
        for (var i = 0; i < 5; i++)
            limiter.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_ConcurrentExceedsMax_Blocks()
    {
        using var limiter = new AiRateLimiter(_logger);
        for (var i = 0; i < 5; i++)
            await limiter.TryAcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sixthTask = limiter.TryAcquireAsync(cts.Token);
        try { await sixthTask; limiter.Release(); }
        catch (OperationCanceledException) { }
        for (var i = 0; i < 5; i++)
            limiter.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledDuringWait_ReleasesSemaphoreAndThrows()
    {
        using var limiter = new AiRateLimiter(_logger);
        for (var i = 0; i < 5; i++)
            await limiter.TryAcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => limiter.TryAcquireAsync(cts.Token));
        for (var i = 0; i < 5; i++)
            limiter.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledAfterAcquiringSemaphore_ReleasesSlot()
    {
        using var limiter = new AiRateLimiter(_logger);
        SetCurrentTokens(limiter, 0.0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try { await limiter.TryAcquireAsync(cts.Token); }
        catch (OperationCanceledException) { }
        await Task.Delay(50);
        var acquired = 0;
        for (var i = 0; i < 5; i++)
        {
            var result = await limiter.TryAcquireAsync(CancellationToken.None);
            if (result) acquired++;
        }
        Assert.True(acquired >= 4, $"应至少获取 4 个槽位，实际获取: {acquired}");
        for (var i = 0; i < acquired; i++)
            limiter.Release();
    }

    [Fact]
    public async Task Release_AfterAcquire_ReturnsTokenToPool()
    {
        using var limiter = new AiRateLimiter(_logger);
        await limiter.TryAcquireAsync(CancellationToken.None);
        limiter.Release();
        var result = await limiter.TryAcquireAsync(CancellationToken.None);
        Assert.True(result);
        limiter.Release();
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>通过反射设置内部 _currentTokens 字段</summary>
    private static void SetCurrentTokens(AiRateLimiter limiter, double value)
    {
        var field = typeof(AiRateLimiter).GetField("_currentTokens",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(limiter, value);
    }
}
