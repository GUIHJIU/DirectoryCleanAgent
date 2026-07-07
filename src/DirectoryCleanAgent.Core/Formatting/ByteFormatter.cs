namespace DirectoryCleanAgent.Core.Formatting;

/// <summary>
/// 字节格式化工具 — 将字节数转换为人类可读字符串。
///
/// 统一 QuarantineViewModel、QuarantineManager、MainViewModel 中
/// 三处重复的 FormatBytes/FormatBytesForDisplay 实现。
/// </summary>
public static class ByteFormatter
{
    /// <summary>
    /// 将字节数格式化为人类可读字符串（GB/MB/KB/B）。
    /// </summary>
    /// <param name="bytes">字节数</param>
    /// <returns>格式化的字符串，如 "1.50 GB"、"256.00 MB"</returns>
    public static string Format(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F2} KB",
            _ => $"{bytes} B"
        };
    }
}
