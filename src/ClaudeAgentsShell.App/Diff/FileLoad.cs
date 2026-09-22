namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Одна загрузка файла по просьбе страницы: своя отмена, связанная с отменой поколения.
/// Повторная просьба того же пути отменяет её, не трогая загрузки других путей.
/// </summary>
/// <remarks>
/// Отмена приходит из потока интерфейса, а её колбэк снимает процесс git — поэтому колбэки
/// исполняются в пуле (<see cref="CancellationTokenSource.CancelAsync"/>), а токен помечается
/// отменённым сразу. Источник освобождается, только когда отработали и колбэки, и сама загрузка.
/// </remarks>
internal sealed class FileLoad : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private Task? _cancelling;
    private bool _disposed;

    /// <inheritdoc cref="FileLoad" />
    /// <param name="cancellation">Отмена, связанная с отменой поколения; загрузка ею владеет.</param>
    public FileLoad(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        Token = cancellation.Token;
    }

    /// <summary>Отмена загрузки.</summary>
    public CancellationToken Token { get; }

    /// <summary>Отменяет загрузку, не исполняя колбэки на вызывающем потоке. Повторно и после освобождения — ничего.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (_disposed || _cancelling is not null)
            {
                return;
            }

            _cancelling = _cancellation.CancelAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task? cancelling;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelling = _cancelling;
        }

        if (cancelling is null || cancelling.IsCompleted)
        {
            _cancellation.Dispose();
            return;
        }

        cancelling.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
