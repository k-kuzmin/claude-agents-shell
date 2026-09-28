namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Идущая отправка файлов в панель вкладки (<c>file.show</c> и части <c>file.content</c>).
/// Идёт вне общего замка отправки; diff, открытый позже, отменяет её и ждёт конца, прежде
/// чем отправить своё — так порядок сообщений вкладки на странице сохраняется.
/// </summary>
/// <remarks>
/// Колбэки отмены исполняются в пуле (<see cref="CancellationTokenSource.CancelAsync"/>):
/// отмена может прийти из потока интерфейса. Источник отмены освобождается после них.
/// </remarks>
internal sealed class FileSend
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _cancelling;
    private bool _completed;

    /// <inheritdoc cref="FileSend" />
    /// <param name="stamp">Отметка запросов diff на момент запроса файлов.</param>
    /// <param name="cancellationToken">Отмена со стороны вызова <c>show_file</c>.</param>
    public FileSend(long stamp, CancellationToken cancellationToken)
    {
        Stamp = stamp;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Token = _cancellation.Token;
    }

    /// <summary>Отметка запросов diff на момент запроса файлов.</summary>
    public long Stamp { get; }

    /// <summary>Отмена отправки.</summary>
    public CancellationToken Token { get; }

    /// <summary>Отменяет отправку; задача завершается, когда отправка действительно закончилась.</summary>
    public Task CancelAsync()
    {
        lock (_sync)
        {
            if (!_completed && _cancelling is null)
            {
                _cancelling = _cancellation.CancelAsync();
            }
        }

        return _done.Task;
    }

    /// <summary>Отправка закончилась: освобождает отмену и отпускает ждущих.</summary>
    public async Task CompleteAsync()
    {
        Task? cancelling;
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            cancelling = _cancelling;
        }

        if (cancelling is not null)
        {
            await cancelling.ConfigureAwait(false);
        }

        _cancellation.Dispose();
        _done.TrySetResult();
    }
}
