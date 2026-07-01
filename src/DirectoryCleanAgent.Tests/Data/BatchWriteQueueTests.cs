using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// BatchWriteQueue 单元测试。
/// 验证批量写入队列的入队、阈值触发、定时刷新、失败重试及资源释放。
/// </summary>
public class BatchWriteQueueTests
{
    private readonly ILogger _logger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task Enqueue_SingleItem_ShouldBeFlushedOnDemand()
    {
        // Arrange
        var flushedBatches = new List<IReadOnlyList<string>>();
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                flushedBatches.Add(batch);
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-single",
            batchSize: 10,
            flushIntervalMs: 1000);

        // Act
        queue.Enqueue("item-1");
        await queue.FlushAsync();

        // Assert
        Assert.Single(flushedBatches);
        Assert.Single(flushedBatches[0]);
        Assert.Equal("item-1", flushedBatches[0][0]);
    }

    [Fact]
    public async Task Enqueue_BatchSizeThreshold_ShouldTriggerAutoFlush()
    {
        // Arrange
        var flushEvent = new ManualResetEventSlim();
        var flushedBatches = new List<IReadOnlyList<string>>();
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                lock (flushedBatches)
                {
                    flushedBatches.Add(batch);
                }
                flushEvent.Set();
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-threshold",
            batchSize: 5,
            flushIntervalMs: 10000); // 长时间间隔，确保是阈值触发而非定时触发

        // Act — 入队 5 条（达到阈值），稍后等第 6 条触发批量
        for (var i = 0; i < 6; i++)
        {
            queue.Enqueue($"item-{i}");
        }

        // 等待自动刷新（阈值触发是异步的）
        flushEvent.Wait(TimeSpan.FromSeconds(3));

        // Assert
        Assert.NotEmpty(flushedBatches);
    }

    [Fact]
    public async Task FlushAsync_EmptyQueue_ShouldNotCallHandler()
    {
        // Arrange
        var handlerCalled = false;
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                handlerCalled = true;
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-empty");

        // Act
        await queue.FlushAsync();

        // Assert
        Assert.False(handlerCalled, "空队列不应触发刷新处理器");
    }

    [Fact]
    public async Task EnqueueBatch_MultipleItems_ShouldBeFlushedTogether()
    {
        // Arrange
        IReadOnlyList<string>? capturedBatch = null;
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                capturedBatch = batch;
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-batch",
            batchSize: 100);

        // Act
        queue.EnqueueBatch(new[] { "a", "b", "c", "d", "e" });
        await queue.FlushAsync();

        // Assert
        Assert.NotNull(capturedBatch);
        Assert.Equal(5, capturedBatch!.Count);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, capturedBatch);
    }

    [Fact]
    public async Task FlushHandler_ThrowsException_ShouldRequeueItems()
    {
        // Arrange
        var handlerCallCount = 0;
        IReadOnlyList<string>? lastBatch = null;
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                handlerCallCount++;
                if (handlerCallCount == 1)
                {
                    // 第一次调用：抛出异常模拟写入失败
                    throw new InvalidOperationException("模拟写入失败");
                }
                // 第二次调用：成功
                lastBatch = batch;
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-retry",
            batchSize: 100);

        // Act
        queue.Enqueue("retry-item");
        await queue.FlushAsync(); // 第一次：handler 抛出异常，条目退回队列

        // 第二次 Flush 应成功
        await queue.FlushAsync();

        // Assert
        Assert.Equal(2, handlerCallCount);
        Assert.NotNull(lastBatch);
        Assert.Single(lastBatch!);
        Assert.Equal("retry-item", lastBatch![0]);
    }

    [Fact]
    public void PendingCount_ShouldReflectQueueSize()
    {
        // Arrange
        using var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) => Task.CompletedTask,
            logger: _logger,
            name: "test-count",
            batchSize: 50);

        // Act & Assert
        Assert.Equal(0, queue.PendingCount);

        queue.Enqueue("item-1");
        Assert.Equal(1, queue.PendingCount);

        queue.EnqueueBatch(new[] { "item-2", "item-3" });
        Assert.Equal(3, queue.PendingCount);
    }

    [Fact]
    public void Dispose_ShouldSetIsDisposed()
    {
        // Arrange
        var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) => Task.CompletedTask,
            logger: _logger,
            name: "test-dispose");

        // Act
        Assert.False(queue.IsDisposed);
        queue.Dispose();

        // Assert
        Assert.True(queue.IsDisposed);
    }

    [Fact]
    public void Enqueue_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) => Task.CompletedTask,
            logger: _logger,
            name: "test-disposed-enqueue");
        queue.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue("should-fail"));
    }

    [Fact]
    public async Task FlushAsync_AfterDispose_WithPendingItems_ShouldStillFlush()
    {
        // Arrange
        IReadOnlyList<string>? capturedBatch = null;
        var queue = new BatchWriteQueue<string>(
            flushHandler: (batch, ct) =>
            {
                capturedBatch = batch;
                return Task.CompletedTask;
            },
            logger: _logger,
            name: "test-final-flush",
            batchSize: 100);
        queue.Enqueue("final-item");

        // Act
        queue.Dispose(); // Dispose 内部会执行最后一次 Flush

        // Assert
        Assert.NotNull(capturedBatch);
        Assert.Single(capturedBatch!);
        Assert.Equal("final-item", capturedBatch![0]);
    }
}
