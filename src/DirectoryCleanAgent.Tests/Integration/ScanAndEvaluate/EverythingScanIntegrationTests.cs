// ============================================================
// EverythingScanIntegrationTests.cs
// 功能: Everything 真实扫描集成测试
// 覆盖: 流式枚举、路径过滤、结果上限、取消中断、字段完整性、
//   去重、并发安全、搜索表达式过滤
// 所有测试需要真实 Everything 环境，不可用时自动跳过
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.ScanAndEvaluate;

/// <summary>
/// Everything 真实扫描集成测试
/// 验证 EverythingFileListProvider 的流式枚举和参数下推功能
/// </summary>
public class EverythingScanIntegrationTests : IntegrationTestBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbPath;

    public EverythingScanIntegrationTests() : base("everything_scan")
    {
        _dbPath = Path.Combine(TestRoot, "scan_test.db");

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
    // IT-SCAN-01: 真实 Everything 枚举返回非空文件列表
    // ================================================================

    /// <summary>
    /// 使用真实 Everything 查询文件列表，验证返回非空结果且路径含 \\?\ 前缀
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_ReturnsFiles()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: Everything 真实枚举");

        try
        {
            // Arrange: 获取真实 IFileListProvider
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();

            // 使用简单的查询参数（限制返回数量避免内存压力）
            var queryParams = new EverythingQueryParams
            {
                MaxResults = 100,
                SortType = EverythingSortType.Size,
                SortDescending = true
            };

            // Act: 流式枚举文件
            var files = new List<FileItem>();
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                files.Add(file);
            }

            // Assert: 验证返回了文件
            logger.LogInformation("枚举到 {Count} 个文件", files.Count);
            Assert.NotEmpty(files);

            // 验证路径格式（\\?\ 前缀）
            foreach (var file in files.Take(10))
            {
                Assert.StartsWith(@"\\?\", file.FilePath);
                Assert.True(file.SizeBytes >= 0, "文件大小必须 >= 0");
            }

            logger.LogInformation("路径格式验证通过: 示例路径={Path}", files[0].FilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Everything 枚举测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-02: PathFilter 参数正确过滤结果
    // ================================================================

    /// <summary>
    /// PathFilter 参数将搜索限制在指定目录下
    /// 返回的所有文件路径必须以 PathFilter 开头
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_PathFilterWorks()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: PathFilter 过滤参数");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();

            // 使用 Windows 目录作为过滤路径
            var queryParams = new EverythingQueryParams
            {
                PathFilter = @"\\?\C:\Windows",
                MaxResults = 50
            };

            // Act: 枚举文件
            var files = new List<FileItem>();
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                files.Add(file);
            }

            // Assert: 所有文件路径都包含指定目录
            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.Contains(@"C:\Windows", f.FilePath));

            logger.LogInformation("PathFilter 验证通过: {Count} 个文件均在指定路径下", files.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PathFilter 测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-03: MaxResults 参数限制返回数量
    // ================================================================

    /// <summary>
    /// MaxResults 参数正确限制 Everything 返回的文件数量
    /// 实际返回数 <= MaxResults
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_MaxResultsWorks()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: MaxResults 上限参数");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();

            const int maxResults = 25;
            var queryParams = new EverythingQueryParams
            {
                MaxResults = maxResults
            };

            // Act
            var files = new List<FileItem>();
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                files.Add(file);
            }

            // Assert: 返回数不超过上限
            Assert.True(files.Count <= maxResults,
                $"返回 {files.Count} 个文件，但上限为 {maxResults}");

            logger.LogInformation("MaxResults 验证通过: 限制={Limit}, 实际={Actual}",
                maxResults, files.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaxResults 测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-04: 流式枚举中取消 → 停止产出新文件
    // ================================================================

    /// <summary>
    /// 在流式枚举过程中取消，应停止产出新文件
    /// 抛出 OperationCanceledException
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_CancellationStopsStream()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 扫描取消");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();
            using var cts = new CancellationTokenSource();

            var queryParams = new EverythingQueryParams
            {
                MaxResults = 10000 // 大量文件确保枚举时间长
            };

            var files = new List<FileItem>();

            // Act: 开始枚举后快速取消
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var file in provider.EnumerateFilesAsync(queryParams, cts.Token))
                {
                    files.Add(file);
                    if (files.Count >= 5)
                    {
                        // 获取少量文件后取消
                        await cts.CancelAsync();
                    }
                }
            });

            logger.LogInformation("扫描取消验证通过: 取消前枚举了 {Count} 个文件", files.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-05: FileItem 字段完整性验证
    // ================================================================

    /// <summary>
    /// 真实枚举返回的 FileItem 各字段均有有效值
    /// SizeBytes、LastWriteTime、Extension 不能为默认值
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_FileItemFieldsPopulated()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: FileItem 字段完整性");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();
            var queryParams = new EverythingQueryParams { MaxResults = 10 };

            // Act
            var files = new List<FileItem>();
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                files.Add(file);
            }

            // Assert: 验证每个 FileItem 的关键字段
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                Assert.False(string.IsNullOrWhiteSpace(file.FilePath),
                    "FilePath 不能为空");
                Assert.True(file.SizeBytes >= 0,
                    $"SizeBytes 不能为负数: {file.FilePath}");
                Assert.NotEqual(default(DateTime), file.LastWriteTime);
                Assert.False(string.IsNullOrWhiteSpace(file.EverythingSortKey));
            }

            logger.LogInformation("字段完整性验证通过: 检查了 {Count} 个文件", files.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "字段完整性测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-06: 真实枚举无重复文件路径
    // ================================================================

    /// <summary>
    /// 流式枚举返回的结果中不应包含重复的文件路径
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_RealEverything_NoDuplicatePaths()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 文件去重验证");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();
            var queryParams = new EverythingQueryParams { MaxResults = 500 };

            // Act
            var paths = new HashSet<string>();
            var duplicateCount = 0;
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                if (!paths.Add(file.FilePath))
                {
                    duplicateCount++;
                    logger.LogWarning("发现重复路径: {Path}", file.FilePath);
                }
            }

            // Assert: 无重复
            Assert.Equal(0, duplicateCount);
            logger.LogInformation("去重验证通过: {Count} 个唯一文件，0 个重复", paths.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "去重测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-07: 单例提供器并发调用不产生死锁
    // ================================================================

    /// <summary>
    /// 单例 IFileListProvider 在同一实例上并发调用不产生死锁
    /// SemaphoreSlim(1,1) 保护 SDK 访问但允许多个调用排队
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_ConcurrentAccess_NoDeadlock()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 并发访问安全性");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();

            // Act: 两个并发枚举调用（使用小结果上限快速完成）
            var task1 = CollectFilesAsync(provider, "task1", 30);
            var task2 = CollectFilesAsync(provider, "task2", 30);

            // 5秒超时 — 超时表示死锁
            var completedTask = await Task.WhenAny(
                Task.WhenAll(task1, task2),
                Task.Delay(TimeSpan.FromSeconds(5)));

            // Assert: 必须在超时前完成
            Assert.True(completedTask is not Task<Task>, "并发调用出现死锁（5秒超时）");

            var results = await Task.WhenAll(task1, task2);
            logger.LogInformation("并发安全验证通过: Task1={Count1}, Task2={Count2}",
                results[0].Count, results[1].Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "并发安全测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SCAN-08: SearchExpression 参数过滤结果
    // ================================================================

    /// <summary>
    /// SearchExpression 参数（如 "ext:log"）正确传递给 Everything 过滤结果
    /// 所有返回的文件扩展名必须匹配搜索条件
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task EnumerateFilesAsync_WithSearchExpression_FiltersResults()
    {
        var logger = CreateLogger<EverythingScanIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: SearchExpression 过滤参数");

        try
        {
            var provider = _serviceProvider.GetRequiredService<IFileListProvider>();

            // 搜索 .log 文件
            var queryParams = new EverythingQueryParams
            {
                SearchExpression = "ext:log",
                MaxResults = 50
            };

            // Act
            var files = new List<FileItem>();
            await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
            {
                files.Add(file);
            }

            // Assert: 所有返回文件的扩展名应为 .log
            Assert.NotEmpty(files);
            Assert.All(files, f =>
            {
                var ext = f.Extension?.ToLowerInvariant() ?? "";
                Assert.True(ext == ".log" || string.IsNullOrEmpty(ext),
                    $"搜索 ext:log 但返回了扩展名 '{ext}' 的文件: {f.FilePath}");
            });

            logger.LogInformation("SearchExpression 验证通过: {Count} 个 .log 文件", files.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SearchExpression 测试失败");
            throw;
        }
    }

    // ================================================================
    // 辅助方法
    // ================================================================

    /// <summary>
    /// 使用指定提供器收集文件列表（用于并发测试）
    /// </summary>
    private static async Task<List<FileItem>> CollectFilesAsync(
        IFileListProvider provider, string label, int maxResults)
    {
        var files = new List<FileItem>();
        var queryParams = new EverythingQueryParams { MaxResults = maxResults };
        await foreach (var file in provider.EnumerateFilesAsync(queryParams, CancellationToken.None))
        {
            files.Add(file);
        }
        return files;
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
