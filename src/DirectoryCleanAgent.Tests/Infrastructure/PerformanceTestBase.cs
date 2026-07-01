// ============================================================
// PerformanceTestBase.cs
// 功能: 性能测试基类
// 提供性能指标采集（时间、内存、GC）+ 断言辅助方法
// 用于 D3 性能测试的前置基础设施
// ============================================================

using System.Diagnostics;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 性能测试结果
/// </summary>
public sealed class PerfResult
{
    /// <summary>操作名称</summary>
    public string OperationName { get; init; } = "";

    /// <summary>执行耗时</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>峰值工作集内存（字节）</summary>
    public long PeakWorkingSet { get; init; }

    /// <summary>GC Gen0 回收次数</summary>
    public int Gen0Collections { get; init; }

    /// <summary>GC Gen1 回收次数</summary>
    public int Gen1Collections { get; init; }

    /// <summary>GC Gen2 回收次数</summary>
    public int Gen2Collections { get; init; }

    /// <summary>操作期间分配的总内存（字节），使用 GC.GetTotalAllocatedBytes</summary>
    public long TotalAllocatedBytes { get; init; }

    /// <summary>吞吐量（操作次数/秒），由调用方设置</summary>
    public double ThroughputItemsPerSecond { get; init; }

    public override string ToString()
    {
        return $"{OperationName}: {Elapsed.TotalMilliseconds:F1}ms | " +
               $"内存峰值={PeakWorkingSet / (1024.0 * 1024.0):F1}MB | " +
               $"分配={TotalAllocatedBytes / (1024.0 * 1024.0):F1}MB | " +
               $"GC(0/1/2)={Gen0Collections}/{Gen1Collections}/{Gen2Collections}";
    }
}

/// <summary>
/// 性能测试抽象基类
/// 提供:
///   1. 性能指标采集（时间、内存峰值、GC 回收次数）
///   2. 性能断言辅助方法
///   3. 预热方法（用于 JIT 编译稳定后的测试）
/// </summary>
public abstract class PerformanceTestBase : IDisposable
{
    private bool _disposed;

    // ============================================================
    // 性能测量方法
    // ============================================================

    /// <summary>
    /// 测量同步操作的性能指标
    /// </summary>
    /// <param name="operationName">操作名称（用于报告）</param>
    /// <param name="action">要测量的操作</param>
    /// <param name="itemCount">处理的项目数量（用于计算吞吐量）</param>
    /// <returns>性能测量结果</returns>
    protected PerfResult Measure(string operationName, Action action, int itemCount = 0)
    {
        // 强制 GC 以获取稳定的基线
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 记录 GC 基线
        int gc0Before = GC.CollectionCount(0);
        int gc1Before = GC.CollectionCount(1);
        int gc2Before = GC.CollectionCount(2);
        long allocatedBefore = GC.GetTotalAllocatedBytes();

        var sw = Stopwatch.StartNew();
        long memoryBefore = Environment.WorkingSet;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            sw.Stop();
            throw new InvalidOperationException($"性能测试操作失败: {operationName}", ex);
        }

        sw.Stop();
        long memoryAfter = Environment.WorkingSet;

        var result = new PerfResult
        {
            OperationName = operationName,
            Elapsed = sw.Elapsed,
            PeakWorkingSet = Math.Max(memoryAfter - memoryBefore, 0),
            Gen0Collections = GC.CollectionCount(0) - gc0Before,
            Gen1Collections = GC.CollectionCount(1) - gc1Before,
            Gen2Collections = GC.CollectionCount(2) - gc2Before,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
            ThroughputItemsPerSecond = itemCount > 0 ? itemCount / sw.Elapsed.TotalSeconds : 0,
        };

        return result;
    }

    /// <summary>
    /// 测量异步操作的性能指标
    /// </summary>
    protected async Task<PerfResult> MeasureAsync(string operationName, Func<Task> action, int itemCount = 0)
    {
        // 强制 GC 基线
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int gc0Before = GC.CollectionCount(0);
        int gc1Before = GC.CollectionCount(1);
        int gc2Before = GC.CollectionCount(2);
        long allocatedBefore = GC.GetTotalAllocatedBytes();

        var sw = Stopwatch.StartNew();
        long memoryBefore = Environment.WorkingSet;

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            sw.Stop();
            throw new InvalidOperationException($"性能测试异步操作失败: {operationName}", ex);
        }

        sw.Stop();
        long memoryAfter = Environment.WorkingSet;

        return new PerfResult
        {
            OperationName = operationName,
            Elapsed = sw.Elapsed,
            PeakWorkingSet = Math.Max(memoryAfter - memoryBefore, 0),
            Gen0Collections = GC.CollectionCount(0) - gc0Before,
            Gen1Collections = GC.CollectionCount(1) - gc1Before,
            Gen2Collections = GC.CollectionCount(2) - gc2Before,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
            ThroughputItemsPerSecond = itemCount > 0 ? itemCount / sw.Elapsed.TotalSeconds : 0,
        };
    }

    // ============================================================
    // 性能断言辅助方法
    // ============================================================

    /// <summary>
    /// 断言内存峰值不超过指定值
    /// 对应设计文档要求: 200万文件内存峰值 ≤200MB
    /// </summary>
    protected static void AssertMemoryBelow(long maxBytes, long actualBytes, string context = "")
    {
        Assert.True(actualBytes <= maxBytes,
            $"内存超标{(!string.IsNullOrEmpty(context) ? $" [{context}]" : "")}: " +
            $"实际={actualBytes / (1024.0 * 1024.0):F1}MB, 上限={maxBytes / (1024.0 * 1024.0):F1}MB");
    }

    /// <summary>
    /// 断言操作耗时不超过指定值
    /// </summary>
    protected static void AssertTimeBelow(TimeSpan maxDuration, TimeSpan actual, string context = "")
    {
        Assert.True(actual <= maxDuration,
            $"耗时超标{(!string.IsNullOrEmpty(context) ? $" [{context}]" : "")}: " +
            $"实际={actual.TotalSeconds:F1}s, 上限={maxDuration.TotalSeconds:F1}s");
    }

    /// <summary>
    /// 断言吞吐量不低于指定值
    /// </summary>
    protected static void AssertThroughputAbove(double minItemsPerSecond, double actual, string context = "")
    {
        Assert.True(actual >= minItemsPerSecond,
            $"吞吐量不达标{(!string.IsNullOrEmpty(context) ? $" [{context}]" : "")}: " +
            $"实际={actual:F1} 项/秒, 最低要求={minItemsPerSecond:F1} 项/秒");
    }

    // ============================================================
    // 预热方法
    // ============================================================

    /// <summary>
    /// 预热 JIT 编译器（在正式测量前运行一次操作以消除 JIT 影响）
    /// </summary>
    protected static void Warmup(Action action, int iterations = 3)
    {
        for (int i = 0; i < iterations; i++)
        {
            action();
        }
    }

    /// <summary>
    /// 预热 JIT（异步版本）
    /// </summary>
    protected static async Task WarmupAsync(Func<Task> action, int iterations = 3)
    {
        for (int i = 0; i < iterations; i++)
        {
            await action();
        }
    }

    public virtual void Dispose()
    {
        _disposed = true;
    }
}
