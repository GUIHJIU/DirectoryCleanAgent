using System.Windows.Input;

namespace DirectoryCleanAgent.ViewModels.Base;

/// <summary>
/// 通用 ICommand 实现，将 Execute 和 CanExecute 委托给外部 Action/Func。
/// 支持通过 RaiseCanExecuteChanged 手动刷新命令可用状态。
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    /// <summary>
    /// 创建 RelayCommand。
    /// </summary>
    /// <param name="execute">执行逻辑委托</param>
    /// <param name="canExecute">可用性判断委托（可选，默认始终可用）</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter) => _execute();

    /// <summary>
    /// 手动触发 CanExecuteChanged，用于 CommandManager 无法自动检测的变更场景。
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// 带泛型参数的 RelayCommand，将命令参数传递给 Execute/CanExecute 委托。
/// </summary>
/// <typeparam name="T">命令参数类型</typeparam>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool> _canExecute;

    /// <summary>
    /// 创建带参数的 RelayCommand。
    /// </summary>
    /// <param name="execute">执行逻辑委托，接收命令参数</param>
    /// <param name="canExecute">可用性判断委托（可选，默认始终可用）</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (_ => true);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute((T?)parameter);

    public void Execute(object? parameter) => _execute((T?)parameter);

    /// <summary>
    /// 手动触发 CanExecuteChanged。
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
