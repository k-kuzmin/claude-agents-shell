using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Единственный источник состояния вкладок (раздел 5.3 ТЗ): сводит события хуков Claude Code
/// и ввод пользователя в <see cref="Domain.TabState"/> и короткие имена сессий.
/// </summary>
/// <remarks>
/// Живёт вне <c>ViewModels</c> намеренно: у координатора нет ни разметки, ни команд —
/// это склейка портов, и полоса вкладок видна ему только через <see cref="ITabStateSink"/>.
/// <para>
/// Вывод агента не разбирается ни здесь, ни где-либо ещё — это запрет раздела 7 CLAUDE.md.
/// Не сработавшие хуки означают вкладку без маркера, а не ошибку.
/// </para>
/// </remarks>
public sealed class SessionStateCoordinator : IDisposable
{
    private readonly IHookListener _hooks;
    private readonly ITerminalWorkspace _workspace;
    private readonly ISessionHistoryReader _history;
    private readonly IUiDispatcher _dispatcher;

    private ITabStateSink? _sink;
    private bool _disposed;

    /// <inheritdoc cref="SessionStateCoordinator" />
    /// <param name="hooks">Приёмник хуков: <c>SessionStart</c>, <c>Stop</c>, <c>SessionEnd</c>.</param>
    /// <param name="workspace">Набор вкладок: сопоставление токена с вкладкой и ввод пользователя.</param>
    /// <param name="history">Чтение транскрипта ради заголовка вкладки.</param>
    /// <param name="dispatcher">Поток интерфейса: хуки приходят из потока пула.</param>
    public SessionStateCoordinator(
        IHookListener hooks,
        ITerminalWorkspace workspace,
        ISessionHistoryReader history,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _hooks = hooks;
        _workspace = workspace;
        _history = history;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Поднимает приёмник хуков и начинает слушать события. Вызывается **до первой вкладки**:
    /// адрес приёмника нужен файлу настроек, который уходит сессии через <c>--settings</c>,
    /// иначе первая сессия запустится без хуков.
    /// </summary>
    /// <param name="sink">Полоса вкладок, которой выставляются состояния и имена.</param>
    /// <param name="cancellationToken">Отмена запуска.</param>
    /// <remarks>
    /// Приёмник поднять не удалось — сессии работают без маркеров состояния, и это штатная
    /// деградация раздела 5.3 ТЗ: исключение наружу не выходит, ошибка пользователю не показывается.
    /// </remarks>
    public Task StartAsync(ITabStateSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;

        // TODO (блок M4-1, раздел 5.3 ТЗ): поднять _hooks, подписаться на HookReceived
        // и на _workspace.UserInputReceived, разложить события по состояниям вкладок.
        _ = _history;
        _ = _dispatcher;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Снимает только свои подписки. Сам <see cref="IHookListener"/> освобождает контейнер:
    /// он его и создал, а двойное освобождение — источник тихих гонок при выходе.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sink = null;

        // TODO (блок M4-1, раздел 5.3 ТЗ): снять подписки на HookReceived и UserInputReceived.
        _ = _hooks;
    }
}
