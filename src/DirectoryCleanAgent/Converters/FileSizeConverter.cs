using System.Globalization;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 将字节数转换为人类可读的文件大小字符串（如 "18.2 GB"）。
/// 支持 B / KB / MB / GB / TB 五级单位自动换算。
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            long bytes = value switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => 0L
            };

            if (bytes <= 0) return "0 B";

            // 逐级换算，找到最合适的单位
            int unitIndex = 0;
            double size = bytes;
            while (size >= 1024.0 && unitIndex < SizeUnits.Length - 1)
            {
                size /= 1024.0;
                unitIndex++;
            }

            // 小于 10 时保留一位小数，否则取整
            return size < 10
                ? $"{size:F1} {SizeUnits[unitIndex]}"
                : $"{size:F0} {SizeUnits[unitIndex]}";
        }
        catch (Exception)
        {
            return "0 B";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("FileSizeConverter 不支持反向转换");
    }
}
