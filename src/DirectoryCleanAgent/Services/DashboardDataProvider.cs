using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Models;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 真实仪表板数据提供者，从 SQLite 决策缓存聚合统计数据。
/// </summary>
public class DashboardDataProvider : IDashboardDataProvider
{
    private readonly IFileDecisionCacheRepository _cacheRepo;
    private readonly IOperationExecutor _operationExecutor;
    private readonly ILogger<DashboardDataProvider> _logger;

    public DashboardDataProvider(
        IFileDecisionCacheRepository cacheRepo,
        IOperationExecutor operationExecutor,
        ILogger<DashboardDataProvider> logger)
    {
        _cacheRepo = cacheRepo ?? throw new ArgumentNullException(nameof(cacheRepo));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<DashboardItem>> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            var allEntries = await _cacheRepo.GetAllAsync(ct).ConfigureAwait(false);

            var groups = allEntries
                .GroupBy(e => e.FinalAction)
                .ToDictionary(g => g.Key, g => (TotalSize: g.Sum(e => e.SizeBytes), Count: g.Count()));

            return new List<DashboardItem>
            {
                CreateItem(FinalAction.AutoDelete, "安全可自动清理", "\U0001f7e2", groups),
                CreateItem(FinalAction.SuggestDelete, "建议清理", "\U0001f4a1", groups),
                CreateItem(FinalAction.ManualReview, "需人工确认", "\u26a0\ufe0f", groups),
                CreateItem(FinalAction.Protected, "已保护", "\U0001f6e1\ufe0f", groups)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载仪表板数据失败");
            return Array.Empty<DashboardItem>();
        }
    }

    public async Task<RecycleBinCapacity> GetRecycleBinCapacityAsync(CancellationToken ct = default)
    {
        try
        {
            return await _operationExecutor.QueryRecycleBinCapacityAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取回收站容量失败");
            return default;
        }
    }

    private static DashboardItem CreateItem(
        FinalAction category, string title, string icon,
        Dictionary<FinalAction, (long TotalSize, int Count)> groups)
    {
        groups.TryGetValue(category, out var data);
        return new DashboardItem
        {
            Category = category,
            Title = title,
            Icon = icon,
            SizeBytes = data.TotalSize,
            ItemCount = data.Count
        };
    }
}
