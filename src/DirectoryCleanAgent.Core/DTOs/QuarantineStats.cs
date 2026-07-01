namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 隔离区使用统计信息 DTO — B6 核心数据模型。
///
/// 通过 IQuarantineManager.GetQuarantineStatsAsync 获取，
/// 用于 UI 展示隔离区状态和容量强制决策。
///
/// 设计决策（遵循 V3.7 设计文档 7.4 节）：
/// - 使用 readonly struct 保持与 RecycleBinCapacity 一致的值语义
/// - DriveFreeBytes 用于大文件绕过判断的卷级别空间参考
/// - ExpiredFileCount 供 UI 展示"可清理 X 个过期文件"提示
/// </summary>
public readonly struct QuarantineStats
{
    /// <summary>隔离区中的 .quarantine 文件数量（不含非 .quarantine 文件）</summary>
    public int TotalFileCount { get; init; }

    /// <summary>隔离区已用空间（字节），所有 .quarantine 文件大小之和</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>隔离区最大容量（来自 UserConfig.QuarantineMaxSizeBytes），0 表示禁用</summary>
    public long QuarantineMaxSizeBytes { get; init; }

    /// <summary>可用容量 = max(0, QuarantineMaxSizeBytes - TotalSizeBytes)</summary>
    public long AvailableBytes { get; init; }

    /// <summary>使用率百分比（0.0 ~ 100.0），QuarantineMaxSizeBytes ≤ 0 时返回 0</summary>
    public double UtilizationPercentage { get; init; }

    /// <summary>超过 QuarantineRetentionDays 的过期文件数量</summary>
    public int ExpiredFileCount { get; init; }

    /// <summary>最旧 .quarantine 文件的时间戳，无文件时返回 null</summary>
    public DateTime? OldestFileTimestamp { get; init; }

    /// <summary>隔离区所在卷的剩余可用空间（字节），用于大文件绕过判断</summary>
    public long DriveFreeBytes { get; init; }

    /// <summary>隔离区是否已禁用（QuarantineMaxSizeBytes ≤ 0）</summary>
    public bool IsDisabled => QuarantineMaxSizeBytes <= 0;

    /// <summary>人类可读的摘要信息</summary>
    public override string ToString() =>
        IsDisabled
            ? "隔离区已禁用"
            : $"隔离区: {TotalFileCount} 个文件, "
              + $"{TotalSizeBytes / 1024.0 / 1024.0:F1} MB / {QuarantineMaxSizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GB "
              + $"({UtilizationPercentage:F1}%), 过期 {ExpiredFileCount} 个";
}
