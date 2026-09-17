using System.Windows.Input;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>Команда, выполняющая синхронное действие.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <inheritdoc cref="RelayCommand" />
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Просит интерфейс перепроверить доступность команды.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Команда, выполняющая асинхронное действие ViewModel.
/// <para>
/// Сам метод ViewModel всегда возвращает <see cref="Task"/> и покрывается тестами напрямую;
/// команда — только обёртка для разметки. Из-за этого в тестах нет гонки между
/// <c>Execute</c> и проверкой состояния.
/// </para>
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    /// <inheritdoc cref="AsyncRelayCommand" />
    /// <param name="execute">Действие ViewModel.</param>
    /// <param name="canExecute">Доступность команды.</param>
    /// <param name="onError">
    /// Куда уходит непойманное исключение. Без него сбой асинхронной команды пропал бы молча:
    /// <see cref="ICommand.Execute"/> возвращает <c>void</c> и ждать задачу некому.
    /// </param>
    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        // Не async void: продолжение ловит сбой явно и отдаёт его обработчику,
        // а не роняет процесс из потока пула.
        var task = _execute(parameter);
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        // Контекст синхронизации есть в потоке интерфейса; в его отсутствие (тесты, фоновый
        // поток) планировщик берётся по умолчанию, иначе FromCurrentSynchronizationContext бросает.
        var scheduler = SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var handler = (Action<Exception>?)state;
                if (completed.Exception is { } exception)
                {
                    handler?.Invoke(exception.GetBaseException());
                }
            },
            _onError,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            scheduler);
    }

    /// <summary>Просит интерфейс перепроверить доступность команды.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
