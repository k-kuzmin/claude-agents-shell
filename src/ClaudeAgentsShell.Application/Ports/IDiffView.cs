using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Страница просит построить diff заново: обновить, сменить <c>-w</c>, базу или рабочее дерево.</summary>
/// <param name="terminalId">Вкладка, в панели которой нажали.</param>
/// <param name="directory">Другое рабочее дерево из списка; <c>null</c> — то же, что было.</param>
/// <param name="baseRef">Другая база; <c>null</c> — та же, что была.</param>
/// <param name="ignoreWhitespace">Сравнение без учёта пробелов.</param>
public sealed class DiffRefreshRequestedEventArgs(TerminalId terminalId, string? directory, string? baseRef, bool ignoreWhitespace)
    : EventArgs
{
    /// <summary>Вкладка.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Другое рабочее дерево; <c>null</c> — прежнее.</summary>
    public string? Directory { get; } = directory;

    /// <summary>Другая база; <c>null</c> — прежняя.</summary>
    public string? BaseRef { get; } = baseRef;

    /// <summary>Без учёта пробелов.</summary>
    public bool IgnoreWhitespace { get; } = ignoreWhitespace;
}

/// <summary>Страница раскрыла файл и просит его содержимое.</summary>
/// <param name="terminalId">Вкладка.</param>
/// <param name="path">Путь из оглавления.</param>
/// <param name="context">Только фрагменты или весь файл.</param>
public sealed class DiffFileRequestedEventArgs(TerminalId terminalId, string path, DiffContext context) : EventArgs
{
    /// <summary>Вкладка.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Путь из оглавления.</summary>
    public string Path { get; } = path;

    /// <summary>Контекст.</summary>
    public DiffContext Context { get; } = context;
}

/// <summary>Пользователь закрыл панель diff (<c>Esc</c> или кнопка).</summary>
/// <param name="terminalId">Вкладка.</param>
public sealed class DiffClosedEventArgs(TerminalId terminalId) : EventArgs
{
    /// <summary>Вкладка.</summary>
    public TerminalId TerminalId { get; } = terminalId;
}

/// <summary>
/// Панель diff на странице терминалов. Живёт в том же WebView2, что и терминалы — отдельного
/// контрола нет (раздел 7 CLAUDE.md), поэтому реализует её тот же мост. Интерфейс отдельный
/// от <see cref="ITerminalBridge"/>: терминалам панель не нужна, и наоборот (ISP).
/// </summary>
/// <remarks>
/// У каждой вкладки своя панель. Панель скрытой вкладки держит только оглавление, загруженное
/// содержимое файлов страница выгружает. Показ панели следует за показом терминала вкладки.
/// </remarks>
public interface IDiffView
{
    /// <summary>Просьба перестроить diff.</summary>
    event EventHandler<DiffRefreshRequestedEventArgs>? RefreshRequested;

    /// <summary>Просьба загрузить файл.</summary>
    event EventHandler<DiffFileRequestedEventArgs>? FileRequested;

    /// <summary>Панель закрыта пользователем.</summary>
    event EventHandler<DiffClosedEventArgs>? Closed;

    /// <summary>Открывает панель вкладки в состоянии «строится» — сразу, до ответа git.</summary>
    ValueTask ShowPendingAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>Показывает оглавление.</summary>
    /// <param name="terminalId">Вкладка.</param>
    /// <param name="index">Оглавление.</param>
    /// <param name="worktrees">Рабочие деревья репозитория для переключателя; может быть пустым.</param>
    /// <param name="note">Пояснение агента над оглавлением; <c>null</c> — нет.</param>
    /// <param name="expandFiles">Файлы, которые раскрыть сразу вне бюджета (из <c>files</c> агента).</param>
    /// <param name="cancellationToken">Отмена.</param>
    ValueTask ShowIndexAsync(
        TerminalId terminalId,
        DiffIndex index,
        IReadOnlyList<GitWorktree> worktrees,
        string? note,
        IReadOnlyList<string> expandFiles,
        CancellationToken cancellationToken);

    /// <summary>
    /// Отправляет содержимое файла. Крупный текст реализация режет на части не больше 1 МБ,
    /// чтобы одно сообщение моста не держало поток интерфейса.
    /// </summary>
    ValueTask ShowFileAsync(TerminalId terminalId, FileDiff file, CancellationToken cancellationToken);

    /// <summary>Сообщение о сбое: всей панели (<paramref name="path"/> = <c>null</c>) или одного файла.</summary>
    ValueTask ShowErrorAsync(TerminalId terminalId, string? path, string message, CancellationToken cancellationToken);

    /// <summary>Плашка «есть изменения — обновить»: агент что-то поменял, пока панель открыта.</summary>
    ValueTask MarkStaleAsync(TerminalId terminalId, CancellationToken cancellationToken);
}
