namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Читает текущую ветку каталога. Источник — <c>.git/HEAD</c>, без запуска <c>git</c>.
/// Порты разрезаны намеренно: панели проектов при первом показе нужно только чтение,
/// слежение подключается отдельно.
/// </summary>
public interface IGitBranchReader
{
    /// <summary>
    /// Имя ветки, либо <c>null</c>, если каталог не репозиторий, HEAD отделён или файл не читается.
    /// Не бросает: отсутствие ветки — норма, а не ошибка.
    /// </summary>
    Task<string?> ReadAsync(string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>Ветка каталога сменилась.</summary>
public sealed class GitBranchChangedEventArgs(string workingDirectory, string? branch) : EventArgs
{
    /// <summary>Каталог, за которым велось слежение.</summary>
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>Новое имя ветки или <c>null</c>.</summary>
    public string? Branch { get; } = branch;
}

/// <summary>
/// Следит за сменой ветки через <see cref="System.IO.FileSystemWatcher"/>.
/// Периодического опроса нет. События дебаунсятся: одна операция git трогает <c>.git</c>
/// многократно, и без дебаунса на каждый <c>git fetch</c> прилетает шквал.
/// </summary>
public interface IGitBranchWatcher : IAsyncDisposable
{
    /// <summary>Ветка отслеживаемого каталога сменилась.</summary>
    event EventHandler<GitBranchChangedEventArgs>? BranchChanged;

    /// <summary>
    /// Начинает следить за каталогом. Повторный вызов для того же каталога безвреден.
    /// Каталог не репозиторий — слежение просто не начинается, это не ошибка.
    /// </summary>
    Task WatchAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Прекращает слежение за каталогом.</summary>
    void Unwatch(string workingDirectory);
}
