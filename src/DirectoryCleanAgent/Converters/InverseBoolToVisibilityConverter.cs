using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 布尔取反到 Visibility 转换器。
/// true → Collapsed, false → Visible。
/// 用于"当某条件不满足时不显示"的场景。
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <summary>静态单例，供 XAML 中 {x:Static} 直接引用</summary>
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;
        return boolValue ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility != Visibility.Visible;
    }
}
