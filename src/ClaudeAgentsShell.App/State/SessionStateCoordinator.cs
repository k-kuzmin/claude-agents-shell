using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

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

    /// <summary>
    /// Чем читать транскрипт вкладки и нужен ли ей ещё заголовок. Трогается только из потока
    /// интерфейса, поэтому обычный словарь без блокировок.
    /// </summary>
    private readonly Dictionary<TerminalId, SessionProbe> _sessions = [];

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private ITabStateSink? _sink;
    private bool _subscribed;
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

        // Токен берётся заранее: после Dispose() обращение к CancellationTokenSource.Token бросает,
        // а незавершённое чтение транскрипта вправе спросить отмену уже после закрытия окна.
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>
    /// Незавершённое чтение транскрипта. Существует ради тестов: в приложении заголовок
    /// догоняет вкладку сам, а тесту надо дождаться, пока он догонит, не опрашивая приёмник.
    /// </summary>
    internal Task PendingTitleWork { get; private set; } = Task.CompletedTask;

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
    public async Task StartAsync(ITabStateSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _sink = sink;

        // Подписка раньше подъёма приёмника: событие, пришедшее в тот же миг, не теряется.
        _hooks.HookReceived += OnHookReceived;
        _workspace.UserInputReceived += OnUserInputReceived;
        _subscribed = true;

        try
        {
            await _hooks.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Порт занят, права на HttpListener не выданы, запуск отменён — вкладки живут
            // без маркеров состояния (раздел 5.3 ТЗ). Подписки остаются: приёмник, который
            // не поднялся, просто никогда не сработает, а снимет их Dispose.
        }
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

        if (_subscribed)
        {
            _hooks.HookReceived -= OnHookReceived;
            _workspace.UserInputReceived -= OnUserInputReceived;
            _subscribed = false;
        }

        _sink = null;
        _sessions.Clear();

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Хук пришёл из потока пула <c>HttpListener</c> — работа переносится в поток интерфейса.</summary>
    private void OnHookReceived(object? sender, HookEventArgs e)
    {
        var hookEvent = e.Event;
        _dispatcher.Post(() => ApplyHook(hookEvent));
    }

    /// <summary>Пользователь что-то ввёл во вкладку — значит, агент снова работает.</summary>
    /// <remarks>
    /// Маршалинга здесь нет намеренно: событие уже поднято в потоке интерфейса (WebView2 отдаёт
    /// <c>WebMessageReceived</c> на своём диспетчере, а набор вкладок передаёт его синхронно).
    /// Это горячий путь ввода — замыкание и очередь диспетчера на каждое нажатие клавиши здесь
    /// неуместны. Не «чинить» добавлением <see cref="IUiDispatcher.Post" />.
    /// <para>
    /// В «работает» переводит любой ввод, а не только первый после <c>Stop</c>, как сказано
    /// в разделе 5.3 ТЗ: у свежей сессии, которая в «ждёт ввода» ещё ни разу не была, точка иначе
    /// не менялась бы вовсе. Отступление от буквы ТЗ согласовано с пользователем.
    /// </para>
    /// </remarks>
    private void OnUserInputReceived(object? sender, TerminalInputEventArgs e) =>
        _sink?.SetState(e.TerminalId, TabState.Busy);

    /// <summary>Раскладывает событие хука по состоянию вкладки. Выполняется в потоке интерфейса.</summary>
    private void ApplyHook(HookEvent hookEvent)
    {
        if (_disposed || _sink is not { } sink)
        {
            return;
        }

        // Неизвестный токен — молча мимо: сессию могли запустить мимо приложения, а вкладку —
        // только что закрыть. Штатная деградация раздела 5.3 ТЗ, ошибку показывать нельзя.
        if (!_workspace.TryResolveTerminal(hookEvent.CorrelationToken, out var terminalId))
        {
            return;
        }

        switch (hookEvent.Kind)
        {
            case HookKind.SessionStart:
                // Началась другая сессия — прежний заголовок больше не её, ищем заново.
                _sessions.Remove(terminalId);
                sink.SetState(terminalId, TabState.Idle);
                RequestTitle(sink, terminalId, hookEvent);
                break;

            case HookKind.Stop:
                sink.SetState(terminalId, TabState.AwaitingInput);

                // Вторая попытка достать заголовок. В момент SessionStart транскрипта могло
                // ещё не быть — файл создаётся не мгновенно; к Stop агент уже ответил, значит
                // первое сообщение пользователя в файле точно есть. Это событие, а не таймер:
                // периодического опроса здесь нет и быть не может (запрет раздела 7 CLAUDE.md).
                RequestTitle(sink, terminalId, hookEvent);
                break;

            case HookKind.SessionEnd:
                _sessions.Remove(terminalId);
                sink.SetState(terminalId, TabState.Unknown);
                break;

            case HookKind.Unknown:
            default:
                // Незарегистрированный хук игнорируется: состояние вкладки не меняется.
                break;
        }
    }

    /// <summary>
    /// Ставит чтение транскрипта ради короткого имени вкладки (раздел 6.3 ТЗ), если оно ещё нужно.
    /// Выполняется в потоке интерфейса: <see cref="ITabStateSink.TryGetWorkingDirectory" /> — его член.
    /// </summary>
    private void RequestTitle(ITabStateSink sink, TerminalId terminalId, HookEvent hookEvent)
    {
        _sessions.TryGetValue(terminalId, out var probe);
        if (probe is { TitleResolved: true })
        {
            return;
        }

        // Значение самого события важнее запомненного: после --resume у вкладки другая сессия.
        var sessionId = FirstFilled(hookEvent.SessionId, probe?.SessionId);
        if (sessionId is null)
        {
            // Без session_id транскрипт не найти. Заголовок остаётся «новая сессия».
            return;
        }

        // Каталог: сначала cwd из хука, потом запомненный, потом каталог самой вкладки.
        var workingDirectory = FirstFilled(hookEvent.WorkingDirectory, probe?.WorkingDirectory);
        if (workingDirectory is null
            && sink.TryGetWorkingDirectory(terminalId, out var tabDirectory)
            && !string.IsNullOrWhiteSpace(tabDirectory))
        {
            workingDirectory = tabDirectory;
        }

        if (workingDirectory is null)
        {
            // Вкладки уже нет — событие опоздало.
            return;
        }

        _sessions[terminalId] = new SessionProbe(sessionId, workingDirectory, TitleResolved: false);

        var load = LoadTitleAsync(sink, terminalId, sessionId, workingDirectory);
        PendingTitleWork = PendingTitleWork.IsCompleted ? load : Task.WhenAll(PendingTitleWork, load);
    }

    /// <summary>
    /// Читает транскрипт и, если из первого сообщения вышло короткое имя, отдаёт его вкладке.
    /// </summary>
    /// <remarks>
    /// Чтение уходит с потока интерфейса через <c>ConfigureAwait(false)</c> и не задерживает
    /// ни отрисовку, ни приём следующих хуков. Метод не бросает: заголовок вкладки — не повод
    /// ронять приложение, а формат <c>.jsonl</c> считается нестабильным (раздел 7 CLAUDE.md).
    /// </remarks>
    private async Task LoadTitleAsync(
        ITabStateSink sink,
        TerminalId terminalId,
        string sessionId,
        string workingDirectory)
    {
        SessionSummary? summary;
        try
        {
            summary = await _history.ReadOneAsync(workingDirectory, sessionId, _lifetimeToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Окно закрылось посреди чтения, файл исчез, разбор сорвался — заголовок просто
            // не обновится, и это не ошибка.
            return;
        }

        // Транскрипта ещё нет либо первое сообщение не разобралось — тоже не ошибка:
        // следующая попытка будет по Stop.
        if (SessionShortTitle.Shorten(summary?.Title) is not { } shortTitle)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_sink, sink))
            {
                return;
            }

            // Пока читали, вкладка могла начать другую сессию — тогда заголовок уже чужой.
            if (_sessions.TryGetValue(terminalId, out var probe) && probe.SessionId == sessionId)
            {
                _sessions[terminalId] = probe with { TitleResolved = true };
                sink.SetShortTitle(terminalId, shortTitle);
            }
        });
    }

    /// <summary>Первое непустое из двух значений; оба пусты — <c>null</c>.</summary>
    private static string? FirstFilled(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>Чем читать транскрипт вкладки и нужен ли ей ещё заголовок.</summary>
    /// <remarks>
    /// Запись живёт от <c>SessionStart</c> до <c>SessionEnd</c>. Вкладка, закрытая пользователем
    /// без <c>SessionEnd</c>, оставляет запись до выхода из приложения: это несколько десятков байт
    /// на вкладку за сеанс, и ради них не стоит заводить ещё одну подписку на жизненный цикл вкладок.
    /// </remarks>
    /// <param name="SessionId">Идентификатор сессии Claude Code из хука.</param>
    /// <param name="WorkingDirectory">Каталог, в котором искать транскрипт.</param>
    /// <param name="TitleResolved">Короткое имя уже выставлено — перечитывать транскрипт незачем.</param>
    private sealed record SessionProbe(string SessionId, string WorkingDirectory, bool TitleResolved);
}
