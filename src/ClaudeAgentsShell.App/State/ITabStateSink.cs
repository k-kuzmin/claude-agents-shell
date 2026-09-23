using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// То, что <see cref="SessionStateCoordinator"/> знает о полосе вкладок: выставить состояние,
/// выставить короткое имя и узнать рабочий каталог вкладки. Больше координатору от интерфейса
/// приложения ничего не нужно, поэтому и не даётся.
/// </summary>
/// <remarks>
/// Все члены вызываются в потоке интерфейса: маршалинг — забота координатора, потому что
/// только он знает, из какого потока пришло событие.
/// </remarks>
public interface ITabStateSink
{
    /// <summary>Выставляет состояние вкладки. Неизвестная вкладка — ничего не делает.</summary>
    void SetState(TerminalId terminalId, TabState state);

    /// <summary>
    /// Заменяет короткое имя сессии (раздел 6.3 ТЗ). Неизвестная вкладка — ничего не делает.
    /// </summary>
    void SetShortTitle(TerminalId terminalId, string shortTitle);

    /// <summary>
    /// Возвращает короткое имя вкладки к «новая сессия»: во вкладке началась другая сессия,
    /// и имя прежней больше не её (раздел 6.3 ТЗ).
    /// </summary>
    /// <remarks>
    /// Звать **только** когда идентификатор сессии действительно сменился. <c>SessionStart</c>
    /// приходит и на <c>/clear</c>, и на сжатие контекста, и на <c>--resume</c>: безусловный
    /// сброс заставил бы заголовок мигать «новая сессия» и обратно на каждом таком событии.
    /// </remarks>
    void ResetShortTitle(TerminalId terminalId);

    /// <summary>
    /// Каталог, в котором запущена сессия вкладки. Нужен, чтобы найти транскрипт сессии,
    /// когда хук не принёс <c>cwd</c>.
    /// </summary>
    /// <remarks>
    /// Это каталог **вкладки**, а не текущий путь её проекта: путь проекта могли сменить
    /// в настройках (раздел 6.5 ТЗ), а псевдоконсоль осталась работать там, где стартовала.
    /// Отдать новый путь значило бы отправить поиск транскрипта в чужой slug.
    /// </remarks>
    /// <returns><c>false</c>, если вкладки уже нет.</returns>
    bool TryGetWorkingDirectory(TerminalId terminalId, out string workingDirectory);

    /// <summary>
    /// Обновляет сведения о сессии вкладки по хуку главного потока. <c>null</c> в аргументе
    /// значит «хук поля не принёс» и прежнее значение не трогает. Неизвестная вкладка — ничего.
    /// </summary>
    /// <param name="terminalId">Вкладка.</param>
    /// <param name="sessionId">Идентификатор сессии из хука.</param>
    /// <param name="currentDirectory"><c>cwd</c> из хука.</param>
    /// <param name="sessionEnded">
    /// <c>true</c> — пришёл <c>SessionEnd</c>, <c>false</c> — <c>SessionStart</c>, <c>null</c> — прочие хуки.
    /// </param>
    void SetSessionContext(TerminalId terminalId, string? sessionId, string? currentDirectory, bool? sessionEnded);
}
