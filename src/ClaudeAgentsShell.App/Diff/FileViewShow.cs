using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Один незавершённый вызов <c>show_file</c> во вкладке: его отмена и сама работа. Новый вызов
/// в ту же вкладку, закрытие панели или вкладки отменяют прежний.
/// </summary>
/// <remarks>
/// Отмена приходит и из потока интерфейса (события панели и вкладок), поэтому колбэки отмены
/// исполняются в пуле (<see cref="CancellationTokenSource.CancelAsync"/>), а источник отмены
/// освобождается только после них.
/// </remarks>
internal sealed class FileViewShow : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _cancelling;
    private bool _disposed;

    /// <inheritdoc cref="FileViewShow" />
    /// <param name="diffStamp"><see cref="IDiffPanelHost.DiffRequestStamp"/> на момент вызова.</param>
    public FileViewShow(long diffStamp)
    {
        DiffStamp = diffStamp;
        Token = _cancellation.Token;
    }

    /// <summary>Отметка запросов diff на момент вызова.</summary>
    public long DiffStamp { get; }

    /// <summary>Отмена работы.</summary>
    public CancellationToken Token { get; }

    /// <summary>Работа вызова; задаётся под замком координатора сразу после создания.</summary>
    public Task<ShowFileOutcome>? Work { get; set; }

    /// <summary>Отменяет работу; повторный вызов и вызов после освобождения ничего не делают.</summary>
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

    /// <summary>Освобождает источник отмены, дождавшись колбэков отмены.</summary>
    public async ValueTask DisposeAsync()
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

        if (cancelling is not null)
        {
            await cancelling.ConfigureAwait(false);
        }

        _cancellation.Dispose();
    }
}
