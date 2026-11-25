using System.Windows.Input;

namespace EqLog.Overlay.Mvvm;

/// <summary>An <see cref="ICommand"/> that delegates to callbacks and ignores its parameter.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">Action run when the command executes.</param>
    /// <param name="canExecute">Optional availability check; omitted means always available.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    /// <summary>Asks bound controls to query <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>An <see cref="ICommand"/> with a typed parameter; parameters of another type are not executable.</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">Action run with the command parameter.</param>
    /// <param name="canExecute">Optional availability check for a parameter.</param>
    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        if (parameter is T typed)
        {
            return _canExecute?.Invoke(typed) ?? true;
        }
        return false;
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (parameter is T typed && CanExecute(typed))
        {
            _execute(typed);
        }
    }

    /// <summary>Asks bound controls to query <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>An <see cref="ICommand"/> running asynchronous work; it is unavailable while a run is in progress.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">Asynchronous work to run.</param>
    /// <param name="canExecute">Optional availability check.</param>
    /// <param name="onError">Optional failure handler; without it a failure faults <see cref="Completion"/>.</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>The most recent run, for callers and tests that need to await completion.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        return !_running && (_canExecute?.Invoke() ?? true);
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        Completion = RunAsync();
    }

    /// <summary>Asks bound controls to query <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync()
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception error) when (_onError is not null)
        {
            _onError(error);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }
}
