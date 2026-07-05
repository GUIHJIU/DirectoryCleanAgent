using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 底部状态栏显示信息的数据模型。
/// 绑定到 MainViewModel.StatusInfo 属性，驱动状态栏文本更新。
/// </summary>
public class StatusBarInfo
{
    /// <summary>本次操作释放的字节数</summary>
    public long FreedBytes { get; set; }

    /// <summary>历史累计清理字节数</summary>
    public long CumulativeBytes { get; set; }

    /// <summary>最后扫描时间（null 表示尚未扫描）</summary>
    public DateTime? LastScanTime { get; set; }

    /// <summary>隔离区当前文件数</summary>
    public int QuarantineCount { get; set; }

    /// <summary>当前操作阶段中文描述（C8: 绑定到状态栏操作文本区）</summary>
    public string OperationStatusText { get; set; } = string.Empty;

    /// <summary>本次释放空间格式化字符串</summary>
    public string FreedBytesText => FormatSize(FreedBytes);

    /// <summary>累计清理格式化字符串</summary>
    public string CumulativeBytesText => FormatSize(CumulativeBytes);

    /// <summary>最后扫描时间格式化字符串</summary>
    public string LastScanTimeText =>
        LastScanTime?.ToString("yyyy-MM-dd HH:mm") ?? "尚未扫描";

    /// <summary>隔离区使用情况文本</summary>
    public string QuarantineText => $"隔离区: {QuarantineCount} 项";

    /// <summary>根据 OperationProgress 更新操作阶段描述文本</summary>
    public void UpdateStatusText(OperationProgress p)
    {
        OperationStatusText = p.Phase switch
        {
            OperationPhase.Scanning when p.TotalCount > 0 => $"正在扫描 ({p.CompletedCount}/{p.TotalCount})...",
            OperationPhase.Scanning => "正在扫描文件...",
            OperationPhase.CheckingCapacity => "正在检查回收站容量...",
            OperationPhase.Hashing when p.TotalCount > 0 => $"正在计算哈希 ({p.CompletedCount}/{p.TotalCount})...",
            OperationPhase.Hashing => "正在计算文件哈希...",
            OperationPhase.Deleting when p.TotalCount > 0 => $"正在删除 ({p.CompletedCount}/{p.TotalCount})...",
            OperationPhase.Deleting => "正在删除...",
            OperationPhase.Verifying => "正在校验已删除文件...",
            OperationPhase.Restoring => "正在恢复文件...",
            OperationPhase.Simulating when p.CompletedCount > 0 => $"正在分析 ({p.CompletedCount} 项)...",
            OperationPhase.Simulating => "正在分析...",
            OperationPhase.Completed => "操作完成",
            OperationPhase.Cancelled => "操作已取消",
            _ => "正在处理..."
        };
    }

    /// <summary>清除操作状态文本（恢复空闲状态）</summary>
    public void ClearStatusText() => OperationStatusText = string.Empty;

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }
        return size < 10
            ? $"{size:F1} {units[unitIndex]}"
            : $"{size:F0} {units[unitIndex]}";
    }
}
