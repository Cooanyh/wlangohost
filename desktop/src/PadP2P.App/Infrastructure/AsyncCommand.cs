using System.Windows.Input;

namespace PadP2P.App.Infrastructure;

internal sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        catch (Exception exception) { onError?.Invoke(exception); }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }
}
