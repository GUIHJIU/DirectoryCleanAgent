using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 树深度 → FontWeight 转换器。
/// Depth=0（根节点）→ SemiBold，Depth>0 → Normal。
/// </summary>
public class DepthToFontWeightConverter : IValueConverter
{
    /// <summary>静态单例，供 XAML 中 {x:Static} 直接引用</summary>
    public static readonly DepthToFontWeightConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int depth = value is int d ? d : 0;
        return depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("DepthToFontWeightConverter 不支持反向转换");
    }
}
