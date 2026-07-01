using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 布尔值到 Visibility 的转换器。
/// true → Visible, false → Collapsed（默认）；
/// 设置 ConverterParameter="Hidden" 时 false → Hidden。
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>静态单例，供 XAML 中 {x:Static} 直接引用</summary>
    public static readonly BoolToVisibilityConverter Instance = new();


    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;
        bool useHidden = parameter is string s && s.Equals("Hidden", StringComparison.OrdinalIgnoreCase);

        return boolValue ? Visibility.Visible
            : useHidden ? Visibility.Hidden
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility == Visibility.Visible;
    }
}
