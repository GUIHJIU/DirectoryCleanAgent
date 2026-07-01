using System.Globalization;
using System.Windows.Data;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// AppState 到布尔值的转换器。
/// 用于控制按钮启用/禁用状态：
/// - 当 AppState 为指定值时返回 true（按钮可用）
/// - 当 AppState 为其他值时返回 false（按钮禁用）
/// ConverterParameter 指定"可用"的状态名称（如 "Ready"）。
/// </summary>
public class AppStateToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is not AppState currentState) return false;
            if (parameter is not string targetStateName) return false;

            // 支持多个状态用逗号分隔（如 "Ready,Scanning"）
            var allowedStates = targetStateName.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var stateName in allowedStates)
            {
                if (Enum.TryParse<AppState>(stateName.Trim(), out var targetState) &&
                    currentState == targetState)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("AppStateToBoolConverter 不支持反向转换");
    }
}
