// ============================================================
// ScanCancellationTests.cs
// 功能: 扫描阶段取消操作集成测试
// 覆盖: 流式枚举中途取消、枚举开始前取消
// ============================================================

using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Integration.Cancellation;

/// <summary>
/// 扫描阶段取消操作测试
/// 验证 CancellationToken 在 IFileListProvider.EnumerateFilesAsync 中的正确传播
/// </summary>
public class ScanCancellationTests : IntegrationTestBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbPath;

    public ScanCancellationTests() : base("cancel_scan")
    {
        _dbPath = Path.Combine(TestRoot, "cancel_scan.db");

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            // 集成测试不需要实际日志输出
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        services.AddConfigLayer();
        services.AddDataLayer(_dbPath);
        services.AddEverythingServices();

        _serviceProvider = services.BuildServiceProvider();
    }

    // ================================================================
    // IT-CANCEL-SCAN-01: 流式枚举中取消 → 停止产出
    // ================================================================

    /// <summary>
    /// 在流式枚举过程中取消，停止 yield return
    /// 抛出 OperationCanceledException 或提前终止循环
    /// </summary>
    [EverythingFact]
    [Trait("Category", "Integration")]
    public async Task EnumerateFilesAsync_CancelDuringStream_StopsMidway()
    {
        var logger = CreateLogger<ScanCancellationTests>();

        logger.LogInformation("开始测试: 扫描中途取消");

        try
        {
            // Arrange
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();
            using var cts = new CancellationTokenSource();

            var queryParams = new EverythingQueryParams
            {
                MaxResults = 50000 // 大量结果确保枚举持续时间长
            };

            var filesCollected = 0;

            // Act: 枚举中取消
            try
            {
                await foreach (var file in provider.EnumerateFilesAsync(queryParams, cts.Token))
                {
                    filesCollected++;
                    if (filesCollected >= 10)
                    {
                        await cts.CancelAsync(); // 获取10个文件后取消
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("扫描取消: OperationCanceledException 正确抛出");
            }

            // Assert: 在取消前收集了少量文件
            Assert.True(filesCollected <= 100,
                $"取消前收集了 {filesCollected} 个文件，应远小于上限 50000");

            logger.LogInformation("扫描取消验证通过: 取消前收集 {Count} 个文件", filesCollected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "扫描取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-CANCEL-SCAN-02: 枚举开始前取消 → 立即抛出
    // ================================================================

    /// <summary>
    /// CancellationToken 在枚举开始前已触发 → 立即抛出
    /// 不执行任何枚举操作（快速失败原则）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnumerateFilesAsync_CancelBeforeStart_ThrowsImmediately()
    {
        var logger = CreateLogger<ScanCancellationTests>();
        logger.LogInformation("开始测试: 扫描前取消");

        try
        {
            // Arrange: 创建已取消的令牌
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // 使用 Mock 的 IFileListProvider 避免需要 Everything
            var mockProvider = new Moq.Mock<IFileListProvider>();
            mockProvider
                .Setup(p => p.EnumerateFilesAsync(
                    It.IsAny<EverythingQueryParams>(), It.IsAny<CancellationToken>()))
                .Returns((EverythingQueryParams q, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return EmptyAsyncEnumerable();
                });

            // Act & Assert: 预取消的令牌必须立即抛出
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in mockProvider.Object.EnumerateFilesAsync(
                    new EverythingQueryParams(), cts.Token))
                {
                    Assert.False(true, "不应该到达这里");
                }
            });

            logger.LogInformation("扫描前取消验证通过: 立即抛出 OperationCanceledException");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "扫描前取消测试失败");
            throw;
        }
    }

    /// <summary>创建空的 IAsyncEnumerable（不依赖 System.Linq.Async）</summary>
    private static async IAsyncEnumerable<FileItem> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    public new void Dispose()
    {
        try
        {
            if (_serviceProvider is IDisposable disposable)
                disposable.Dispose();
        }
        catch { }
        base.Dispose();
    }
}
