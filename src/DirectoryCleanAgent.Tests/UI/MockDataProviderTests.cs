using Microsoft.Extensions.Logging;
using Moq;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Services;
using Xunit;

namespace DirectoryCleanAgent.Tests.UI;

/// <summary>
/// DashboardDataProvider 单元测试。
/// 验证真实仪表板数据提供者从 SQLite 缓存聚合数据的正确性。
/// </summary>
public class DashboardDataProviderTests
{
    private readonly Mock<IFileDecisionCacheRepository> _cacheRepoMock;
    private readonly Mock<IOperationExecutor> _operationExecutorMock;
    private readonly DashboardDataProvider _provider;

    public DashboardDataProviderTests()
    {
        _cacheRepoMock = new Mock<IFileDecisionCacheRepository>();
        _operationExecutorMock = new Mock<IOperationExecutor>();
        var loggerMock = new Mock<ILogger<DashboardDataProvider>>();
        _provider = new DashboardDataProvider(
            _cacheRepoMock.Object,
            _operationExecutorMock.Object,
            loggerMock.Object);
    }

    /// <summary>
    /// 验证 GetDashboardSummaryAsync 返回恰好 4 张卡片，覆盖所有 FinalAction 分类。
    /// </summary>
    [Fact]
    public async Task GetDashboardSummaryAsync_ReturnsFourCards()
    {
        var mockEntries = new List<FileDecisionCache>
        {
            new() { FilePath = @"\\?\C:\test1.tmp", SizeBytes = 1000, LastWriteTime = DateTime.Now, RuleVerdict = RuleVerdict.AutoDelete, SemanticCategory = "临时文件", FinalAction = FinalAction.AutoDelete, CacheVersion = 1 },
            new() { FilePath = @"\\?\C:\test2.log", SizeBytes = 2000, LastWriteTime = DateTime.Now, RuleVerdict = RuleVerdict.SuggestDelete, SemanticCategory = "日志文件", FinalAction = FinalAction.SuggestDelete, CacheVersion = 1 },
            new() { FilePath = @"\\?\C:\test3.dmp", SizeBytes = 3000, LastWriteTime = DateTime.Now, RuleVerdict = RuleVerdict.SuggestDelete, SemanticCategory = "转储文件", FinalAction = FinalAction.ManualReview, CacheVersion = 1 },
            new() { FilePath = @"\\?\C:\test4.dll", SizeBytes = 4000, LastWriteTime = DateTime.Now, RuleVerdict = RuleVerdict.Protect, SemanticCategory = "系统文件", FinalAction = FinalAction.Protected, CacheVersion = 1 },
        };

        _cacheRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockEntries.AsReadOnly());

        var items = await _provider.GetDashboardSummaryAsync();

        Assert.Equal(4, items.Count);
        Assert.Contains(items, i => i.Category == FinalAction.AutoDelete);
        Assert.Contains(items, i => i.Category == FinalAction.SuggestDelete);
        Assert.Contains(items, i => i.Category == FinalAction.ManualReview);
        Assert.Contains(items, i => i.Category == FinalAction.Protected);

        var autoDelete = items.First(i => i.Category == FinalAction.AutoDelete);
        Assert.Equal(1000, autoDelete.SizeBytes);
        Assert.Equal(1, autoDelete.ItemCount);
    }

    /// <summary>
    /// 验证缓存为空时返回 4 张零数据的卡片。
    /// </summary>
    [Fact]
    public async Task GetDashboardSummaryAsync_EmptyCache_ReturnsZeroCards()
    {
        _cacheRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileDecisionCache>().AsReadOnly() as IReadOnlyList<FileDecisionCache>);

        var items = await _provider.GetDashboardSummaryAsync();

        Assert.Equal(4, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(0, item.SizeBytes);
            Assert.Equal(0, item.ItemCount);
        }
    }

    /// <summary>
    /// 验证仓储异常时返回空列表而非抛出异常。
    /// </summary>
    [Fact]
    public async Task GetDashboardSummaryAsync_RepositoryThrows_ReturnsEmpty()
    {
        _cacheRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB error"));

        var items = await _provider.GetDashboardSummaryAsync();

        Assert.Empty(items);
    }

    /// <summary>
    /// 验证回收站容量获取正确转发到 IOperationExecutor。
    /// </summary>
    [Fact]
    public async Task GetRecycleBinCapacityAsync_ReturnsFromExecutor()
    {
        var expected = new RecycleBinCapacity
        {
            TotalBytes = 100_000,
            UsedBytes = 50_000,
            AvailableBytes = 50_000
        };

        _operationExecutorMock
            .Setup(e => e.QueryRecycleBinCapacityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var capacity = await _provider.GetRecycleBinCapacityAsync();

        Assert.Equal(expected.TotalBytes, capacity.TotalBytes);
        Assert.Equal(expected.UsedBytes, capacity.UsedBytes);
        Assert.Equal(expected.AvailableBytes, capacity.AvailableBytes);
        Assert.True(capacity.CanAccommodate(10_000));
    }
}
