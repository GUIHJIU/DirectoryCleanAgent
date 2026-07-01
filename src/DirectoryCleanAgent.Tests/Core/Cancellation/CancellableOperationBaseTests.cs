using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Cancellation;

/// <summary>
/// CancellableOperationBase 取消令牌行为测试。
/// </summary>
public class CancellableOperationBaseTests
{
    /// <summary>测试用的具体实现类</summary>
    private sealed class TestOperation : CancellableOperationBase
    {
        public TestOperation(ILogger logger) : base(logger) { }

        public new void CreateCancellationTokenSource(
            CancellationToken externalToken = default, TimeSpan? timeout = null)
            => base.CreateCancellationTokenSource(externalToken, timeout);

        public new void ThrowIfCancellationRequested()
            => base.ThrowIfCancellationRequested();

        public new async Task ExecuteCancellableAsync(
            Func<CancellationToken, Task> operation, string operationName,
            CancellationToken externalToken = default, TimeSpan? timeout = null)
            => await base.ExecuteCancellableAsync(operation, operationName, externalToken, timeout);
    }

    [Fact]
    public async Task ExecuteCancellableAsync_CompletesSuccessfully()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var op = new TestOperation(logger);
        var completed = false;

        // Act
        await op.ExecuteCancellableAsync(
            async (ct) =>
            {
                await Task.Delay(10, ct);
                completed = true;
            },
            "测试操作");

        // Assert
        Assert.True(completed);
    }

    [Fact]
    public async Task ExecuteCancellableAsync_ExternalTokenCancel_ThrowsOperationCanceledException()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var op = new TestOperation(logger);
        var cts = new CancellationTokenSource();

        // 立即取消外部 Token
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await op.ExecuteCancellableAsync(
                async (ct) =>
                {
                    await Task.Delay(5000, ct); // 长延时，但应被立即取消
                },
                "测试操作",
                externalToken: cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteCancellableAsync_ShortTimeout_ThrowsOperationCanceledException()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var op = new TestOperation(logger);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await op.ExecuteCancellableAsync(
                async (ct) =>
                {
                    await Task.Delay(5000, ct); // 超过超时时间
                },
                "测试操作",
                timeout: TimeSpan.FromMilliseconds(10));
        });
    }

    [Fact]
    public void DefaultTimeout_Is120Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), CancellableOperationBase.DefaultTimeout);
    }

    [Fact]
    public void ThrowIfCancellationRequested_BeforeCreatingCts_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var op = new TestOperation(logger);

        // 未创建 CTS 时调用 ThrowIfCancellationRequested 不应抛出
        op.ThrowIfCancellationRequested();
        Assert.True(true); // 到达此处即成功
    }
}
