using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Models;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 仪表板数据提供者接口。
/// 从 SQLite 决策缓存和操作执行器获取真实统计数据。
/// </summary>
public interface IDashboardDataProvider
{
    /// <summary>
    /// 获取仪表板 4 张卡片的统计数据（按 FinalAction 分组聚合）。
    /// </summary>
    Task<IReadOnlyList<DashboardItem>> GetDashboardSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取回收站实时容量信息。
    /// </summary>
    Task<RecycleBinCapacity> GetRecycleBinCapacityAsync(CancellationToken ct = default);
}
