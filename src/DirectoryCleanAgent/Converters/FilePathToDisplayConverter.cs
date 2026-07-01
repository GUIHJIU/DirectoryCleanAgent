using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 文件路径 → 显示用短名称转换器。
/// 默认模式：将完整路径（含 \\?\ 前缀）转换为仅文件名。
/// ConverterParameter="FullPath"：返回原始完整路径。
/// ConverterParameter="Directory"：返回父目录路径。
/// </summary>
public class FilePathToDisplayConverter : IValueConverter
{
    /// <summary>
    /// 将文件路径转换为显示文本。
    /// 自动去除 \\?\ 前缀，默认返回文件名。
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            string? path = value as string;
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            // 去除 \\?\ 前缀以获得可读路径
            string cleanPath = path.StartsWith(@"\\?\") ? path[4..] : path;

            string mode = parameter as string ?? string.Empty;

            return mode switch
            {
                "FullPath" => cleanPath,
                "Directory" => Path.GetDirectoryName(cleanPath) ?? cleanPath,
                _ => Path.GetFileName(cleanPath) // 默认：仅文件名
            };
        }
        catch
        {
            return value?.ToString() ?? string.Empty;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("FilePathToDisplayConverter 不支持反向转换");
    }
}
