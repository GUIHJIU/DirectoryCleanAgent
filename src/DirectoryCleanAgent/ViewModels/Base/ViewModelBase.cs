using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DirectoryCleanAgent.ViewModels.Base;

/// <summary>
/// MVVM ViewModel 基类，提供 INotifyPropertyChanged 的标准实现。
/// 所有 ViewModel 继承此类以获得属性变更通知能力。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发 PropertyChanged 事件，通知 UI 绑定源已变更。
    /// 调用方通常无需传 propertyName，编译器会自动填充调用方名称。
    /// </summary>
    /// <param name="propertyName">属性名称（自动填充）</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置字段值并在值变更时自动触发 PropertyChanged。
    /// 返回 true 表示值已更新，false 表示新旧值相同无需更新。
    /// </summary>
    /// <typeparam name="T">字段类型</typeparam>
    /// <param name="field">字段引用</param>
    /// <param name="value">新值</param>
    /// <param name="propertyName">属性名称（自动填充）</param>
    /// <returns>值是否已变更</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
