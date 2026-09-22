namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Одна загрузка файла по просьбе страницы: своя отмена, связанная с отменой поколения.
/// Повторная просьба того же пути отменяет её, не трогая загрузки других путей.
/// </summary>
internal sealed class FileLoad : IDisposable
{
    private readonly CancellationTokenSource _cancellation;

    /// <inheritdoc cref="FileLoad" />
    /// <param name="cancellation">Отмена, связанная с отменой поколения; загрузка ею владеет.</param>
    public FileLoad(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        Token = cancellation.Token;
    }

    /// <summary>Отмена загрузки.</summary>
    public CancellationToken Token { get; }

    /// <summary>Отменяет загрузку. Уже закончившаяся и освобождённая — ничего не делает.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Загрузка успела закончиться между поиском и отменой — отменять нечего.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cancellation.Dispose();
}
