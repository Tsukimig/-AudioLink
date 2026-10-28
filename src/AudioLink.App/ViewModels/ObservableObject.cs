using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AudioLink.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class RelayCommand(Action<object?> execute, Func<bool>? canExecute = null, Func<object?, bool>? canExecuteParameter = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => (canExecute?.Invoke() ?? true) && (canExecuteParameter?.Invoke(parameter) ?? true);
    public void Execute(object? parameter) => execute(parameter);
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
