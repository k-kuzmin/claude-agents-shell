namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Склеивает пачку сигналов в один вызов: действие выполняется через заданную задержку
/// после последнего <see cref="Signal" />. Время берётся из <see cref="TimeProvider" />,
/// поэтому поведение проверяется тестом без ожиданий на реальных часах.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Func<CancellationToken, Task> _action;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ITimer _timer;
    private readonly object _sync = new();

    private bool _disposed;

    /// <param name="delay">Окно тишины, после которого выполняется действие.</param>
    /// <param name="action">Действие; его исключения гасятся — сигнал не должен валить поток.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public Debouncer(TimeSpan delay, Func<CancellationToken, Task> action, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _delay = delay;
        _action = action;
        _timer = timeProvider.CreateTimer(
            static state => ((Debouncer)state!).OnElapsed(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Отодвигает выполнение действия на полное окно. Безопасен из любого потока.</summary>
    public void Signal()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Снимает взведённый таймер и отменяет токен действия. Уже начатое действие
    /// не дожидается: оно обязано проверять токен перед побочным эффектом.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _cancellation.Cancel();
        _timer.Dispose();
        _cancellation.Dispose();
    }

    private void OnElapsed()
    {
        CancellationToken token;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            token = _cancellation.Token;
        }

        _ = RunAsync(token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Дебаунсер снят — это штатное завершение, а не сбой.
        }
        catch (Exception)
        {
            // Ветка — справочное поле панели проектов: её сбой не показывается и не роняет приложение.
        }
    }
}
