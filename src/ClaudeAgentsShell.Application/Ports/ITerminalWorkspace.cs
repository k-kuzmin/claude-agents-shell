using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Вкладка завершилась: процесс оболочки вышел.</summary>
public sealed class TerminalExitedEventArgs(TerminalId terminalId, int exitCode) : EventArgs
{
    /// <summary>Завершившаяся вкладка.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Код выхода оболочки.</summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Набор открытых вкладок. Владеет парами «PTY ↔ терминал на странице» и маршрутизацией
/// между ними. О проектах знает ровно столько, сколько нужно для запуска: рабочий каталог,
/// оболочка, команда запуска.
/// </summary>
/// <remarks>
/// Вкладка не закрывается сама при выходе <c>claude</c> — пользователь остаётся в живой
/// оболочке. Закрытие происходит только когда завершился сам процесс оболочки
/// (раздел 5.1 ТЗ), и тогда вкладка остаётся на экране с пометкой о коде выхода
/// (раздел 8 ТЗ) до явного закрытия пользователем.
/// </remarks>
public interface ITerminalWorkspace : IAsyncDisposable
{
    /// <summary>Процесс вкладки завершился.</summary>
    event EventHandler<TerminalExitedEventArgs>? TerminalExited;

    /// <summary>Открытые вкладки в порядке открытия.</summary>
    IReadOnlyList<TerminalId> Terminals { get; }

    /// <summary>Поднимает движок страницы. Вызывается один раз при старте окна.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Открывает вкладку: поднимает оболочку проекта в новой псевдоконсоли и пишет в её stdin
    /// команду запуска. Возвращает идентификатор созданной вкладки.
    /// </summary>
    /// <exception cref="PtyStartException">Псевдоконсоль или процесс создать не удалось.</exception>
    /// <exception cref="ShellNotFoundException">Ни одна оболочка не найдена.</exception>
    Task<TerminalId> OpenAsync(ProjectDefinition project, SessionLaunch launch, CancellationToken cancellationToken);

    /// <summary>
    /// Делает вкладку видимой. Переключение — смена видимости контейнера на странице:
    /// ни пересоздания терминала, ни перерисовки буфера, ни ресайза.
    /// </summary>
    Task ActivateAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>Закрывает вкладку: гасит помпу, освобождает псевдоконсоль, убирает терминал со страницы.</summary>
    Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken);
}
