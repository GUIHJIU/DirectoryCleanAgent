namespace DirectoryCleanAgent.Tests.Core.Progress;

/// <summary>
/// ProgressReport 和 IProgressReporter 功能测试。
/// </summary>
public class ProgressReporterTests
{
    [Fact]
    public void ProgressReport_Percentage_CalculatesCorrectly()
    {
        var report = new ProgressReport
        {
            CompletedCount = 50,
            TotalCount = 200,
            Message = "正在处理..."
        };

        Assert.Equal(25.0, report.Percentage);
        Assert.False(report.IsCompleted);
    }

    [Fact]
    public void ProgressReport_Completed_IsCompletedTrue()
    {
        var report = new ProgressReport
        {
            CompletedCount = 100,
            TotalCount = 100,
            Message = "完成"
        };

        Assert.True(report.IsCompleted);
        Assert.Equal(100.0, report.Percentage);
    }

    [Fact]
    public void ProgressReport_Indeterminate_ReturnsNegativePercentage()
    {
        var report = ProgressReport.Indeterminate("流式扫描中...");

        Assert.Equal(-1, report.TotalCount);
        Assert.Equal(-1.0, report.Percentage);
        Assert.False(report.IsCompleted);
    }

    [Fact]
    public void ProgressReport_Completed_StaticFactory()
    {
        var report = ProgressReport.Completed(500, "操作成功完成");

        Assert.Equal(500, report.CompletedCount);
        Assert.Equal(500, report.TotalCount);
        Assert.True(report.IsCompleted);
        Assert.Equal("操作成功完成", report.Message);
    }

    [Fact]
    public void DefaultProgressReporter_Report_DoesNotThrow_WhenNoProgress()
    {
        var reporter = new DefaultProgressReporter(progress: null, logger: null);
        reporter.Report(new ProgressReport { CompletedCount = 1, TotalCount = 10 });
        // 无异常即通过
        Assert.True(true);
    }

    [Fact]
    public void CompositeProgressReporter_DistributesToAllSubReporters()
    {
        // Arrange — 使用同步回调的 IProgress<T> 替代 Progress<T>（后者在 xUnit 测试中可能通过 SynchronizationContext 异步派发）
        int report1Count = 0, report2Count = 0;
        var sub1 = new SyncProgress<ProgressReport>(_ => report1Count++);
        var sub2 = new SyncProgress<ProgressReport>(_ => report2Count++);
        var composite = new CompositeProgressReporter<ProgressReport>(sub1, sub2);

        // Act
        composite.Report(new ProgressReport { CompletedCount = 1, TotalCount = 10 });

        // Assert — 每个子报告器都被调用一次
        Assert.Equal(1, report1Count);
        Assert.Equal(1, report2Count);
    }

    /// <summary>同步 IProgress&lt;T&gt; 实现，直接调用回调，不经过 SynchronizationContext</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public void ProgressReport_ToString_FormatsCorrectly()
    {
        var report = new ProgressReport
        {
            CompletedCount = 5,
            TotalCount = 10,
            Message = "正在计算哈希"
        };

        var str = report.ToString();
        Assert.Contains("5/10", str);
        Assert.Contains("50.0%", str);
        Assert.Contains("正在计算哈希", str);
    }
}
