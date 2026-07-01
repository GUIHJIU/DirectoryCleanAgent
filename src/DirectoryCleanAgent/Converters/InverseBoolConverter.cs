using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 布尔值取反转换器。true → false, false → true。
/// 典型场景：IsOperating=true 时隐藏"开始"按钮。
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b;
    }
}
