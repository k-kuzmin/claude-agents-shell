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
    /// Рабочий каталог проекта, в котором открыта вкладка. Нужен, чтобы найти транскрипт
    /// сессии, когда хук не принёс <c>cwd</c>.
    /// </summary>
    /// <returns><c>false</c>, если вкладки уже нет.</returns>
    bool TryGetWorkingDirectory(TerminalId terminalId, out string workingDirectory);
}
