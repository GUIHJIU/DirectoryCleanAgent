namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 回收站容量信息 DTO。
/// 通过 SHQueryRecycleBin API 获取，仅在删除操作前调用一次（快速失败原则）。
/// 若待删文件总大小超过可用空间，在生成快照之前即弹出警告。
/// </summary>
public readonly struct RecycleBinCapacity
{
    /// <summary>回收站总容量（字节），0 表示未设置上限</summary>
    public long TotalBytes { get; init; }

    /// <summary>回收站当前已使用容量（字节）</summary>
    public long UsedBytes { get; init; }

    /// <summary>回收站当前可用容量（字节）</summary>
    public long AvailableBytes { get; init; }

    /// <summary>可用空间是否足以容纳指定大小的文件</summary>
    public bool CanAccommodate(long requiredBytes) =>
        TotalBytes == 0 || AvailableBytes >= requiredBytes;

    /// <summary>可用空间占总容量的百分比，若未设置上限则返回 100</summary>
    public double AvailablePercentage =>
        TotalBytes > 0
            ? Math.Min(100.0, (double)AvailableBytes / TotalBytes * 100.0)
            : 100.0;

    public override string ToString() =>
        TotalBytes == 0
            ? "回收站无容量限制"
            : $"回收站: {AvailableBytes / 1024.0 / 1024.0 / 1024.0:F2} GB 可用 / "
              + $"{TotalBytes / 1024.0 / 1024.0 / 1024.0:F2} GB 总量 "
              + $"({AvailablePercentage:F1}%)";
}
