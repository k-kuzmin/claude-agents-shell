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
    /// Открывает вкладку: создаёт терминал на странице и, когда та отчитается о готовности,
    /// поднимает оболочку проекта в псевдоконсоли и пишет в её stdin команду запуска.
    /// Возвращает идентификатор созданной вкладки.
    /// </summary>
    /// <remarks>
    /// Псевдоконсоль поднимается **после** возврата из метода, поэтому сбой её создания сюда
    /// не приходит: он сообщается текстом в саму вкладку и событием <see cref="TerminalExited"/>
    /// с ненулевым кодом. Подписчик обязан считать вкладку неживой по этому событию —
    /// иначе она навсегда останется «работающей» без псевдоконсоли под ней.
    /// </remarks>
    /// <exception cref="ShellNotFoundException">Ни одна оболочка не найдена.</exception>
    Task<TerminalId> OpenAsync(ProjectDefinition project, SessionLaunch launch, CancellationToken cancellationToken);

    /// <summary>
    /// Делает вкладку видимой. Переключение — смена видимости контейнера на странице:
    /// ни пересоздания терминала, ни перерисовки буфера, ни ресайза.
    /// </summary>
    Task ActivateAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>Закрывает вкладку: гасит помпу, освобождает псевдоконсоль, убирает терминал со страницы.</summary>
    Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>
    /// Находит вкладку по токену, который хук вернул в <see cref="Domain.HookEvent.CorrelationToken"/>.
    /// Токен выдаётся вкладке при запуске и уходит в окружение псевдоконсоли, поэтому карта
    /// «токен → вкладка» живёт здесь же, где сами вкладки.
    /// </summary>
    /// <remarks>
    /// Неизвестный токен — <c>false</c>, а не ошибка: сессия могла быть запущена мимо приложения,
    /// а вкладку могли только что закрыть. Приёмник хуков слушает обычный loopback-порт,
    /// доступный любому локальному процессу, поэтому токен обязан быть случайным и не выводимым
    /// из идентификатора вкладки — тот уходит на страницу в каждом сообщении моста.
    /// </remarks>
    bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId);
}
