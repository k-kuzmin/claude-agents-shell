using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>Вкладка закрыта пользователем или вместе с проектом.</summary>
/// <param name="terminalId">Закрытая вкладка.</param>
public sealed class DiffTabClosedEventArgs(TerminalId terminalId) : EventArgs
{
    /// <summary>Закрытая вкладка.</summary>
    public TerminalId TerminalId { get; } = terminalId;
}

/// <summary>
/// Ровно то, что координатору diff нужно от набора вкладок: найти вкладку по токену хука
/// и по идентификатору, узнать о её закрытии. Реализует корневая ViewModel.
/// </summary>
/// <remarks>
/// Все члены вызываются и все события поднимаются <b>в потоке интерфейса</b>: вкладки —
/// объекты ViewModel. Активность вкладки берётся из <see cref="TabViewModel.IsActive"/>,
/// поэтому события «вкладка стала активной» здесь нет — координатор слушает само свойство
/// и так видит любой путь смены вкладки.
/// </remarks>
public interface IDiffTabs
{
    /// <summary>Вкладка закрыта; её панель diff больше не нужна.</summary>
    event EventHandler<DiffTabClosedEventArgs>? TabClosed;

    /// <summary>
    /// Находит вкладку по токену, который вернул хук или вызов <c>show_diff</c>.
    /// Неизвестный токен — <c>false</c>.
    /// </summary>
    bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId);

    /// <summary>Открытая вкладка среди всех проектов; <c>null</c>, если её уже нет.</summary>
    TabViewModel? Find(TerminalId terminalId);
}
