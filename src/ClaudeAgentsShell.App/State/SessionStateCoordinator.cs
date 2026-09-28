using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Единственный источник состояния вкладок (раздел 5.3 ТЗ): сводит события хуков Claude Code
/// в <see cref="Domain.TabState"/> и короткие имена сессий.
/// </summary>
/// <remarks>
/// Живёт вне <c>ViewModels</c> намеренно: у координатора нет ни разметки, ни команд —
/// это склейка портов, и полоса вкладок видна ему только через <see cref="ITabStateSink"/>.
/// <para>
/// Состояние выводится **только** из хуков (раздел 7 CLAUDE.md): ни вывод агента, ни ввод
/// с клавиатуры источником не являются. Ввод не годится принципиально — страница отдаёт одним
/// каналом с нажатиями ответы терминала на запросы программы, поэтому простое переключение
/// вкладок выглядело как ввод пользователя и сбивало «ждёт ввода».
/// </para>
/// <para>
/// Не сработавшие хуки означают вкладку без маркера, а не ошибку.
/// </para>
/// </remarks>
public sealed class SessionStateCoordinator : IDisposable
{
    /// <summary>Значение <c>source</c> у <c>SessionStart</c> новой сессии, начатой с нуля.</summary>
    private const string StartupSource = "startup";

    /// <summary>Значение <c>source</c> у <c>SessionStart</c> после <c>/clear</c>: сессия тоже новая.</summary>
    private const string ClearSource = "clear";

    private readonly IHookListener _hooks;
    private readonly ITerminalWorkspace _workspace;
    private readonly ISessionHistoryReader _history;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>
    /// Сессия вкладки и где лежит её транскрипт — см. <see cref="SessionProbe"/>. Трогается только
    /// из потока интерфейса, поэтому обычный словарь без блокировок.
    /// </summary>
    private readonly Dictionary<TerminalId, SessionProbe> _sessions = [];

    /// <summary>
    /// Что известно о вкладке прямо сейчас и как её меняют хуки — см. <see cref="TabActivity"/>.
    /// Трогается только из потока интерфейса, поэтому обычный словарь без блокировок.
    /// </summary>
    /// <remarks>
    /// Последнее состояние хранится там, а не спрашивается у <see cref="ITabStateSink"/>:
    /// порт остаётся узким, а переходам оно нужно — чтобы опоздавший хук не сбил уже
    /// выставленный маркер.
    /// </remarks>
    private readonly Dictionary<TerminalId, TabActivity> _activity = [];

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private ITabStateSink? _sink;
    private bool _subscribed;
    private bool _disposed;

    /// <inheritdoc cref="SessionStateCoordinator" />
    /// <param name="hooks">
    /// Приёмник хуков: <c>SessionStart</c>, <c>UserPromptSubmit</c>, <c>Stop</c>,
    /// <c>StopFailure</c>, <c>SubagentStart</c>, <c>SubagentStop</c>, <c>PostToolBatch</c>,
    /// <c>PermissionRequest</c>, <c>SessionEnd</c>.
    /// </param>
    /// <param name="workspace">Набор вкладок: сопоставление токена хука с вкладкой.</param>
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

        // Смерть процесса — второй источник, из которого координатор узнаёт, что состояние
        // вкладки больше не действительно. Хуком она не приходит: у убитой оболочки
        // `SessionEnd` выполнять уже некому.
        _workspace.TerminalExited += OnTerminalExited;
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
            _workspace.TerminalExited -= OnTerminalExited;
            _subscribed = false;
        }

        _sink = null;
        _sessions.Clear();
        _activity.Clear();

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Хук пришёл из потока пула <c>HttpListener</c> — работа переносится в поток интерфейса.</summary>
    private void OnHookReceived(object? sender, HookEventArgs e)
    {
        var hookEvent = e.Event;
        _dispatcher.Post(() => ApplyHook(hookEvent));
    }

    /// <summary>
    /// Процесс вкладки завершился. Событие приходит из фонового потока помпы — работа
    /// переносится в поток интерфейса.
    /// </summary>
    private void OnTerminalExited(object? sender, TerminalExitedEventArgs e)
    {
        var terminalId = e.TerminalId;
        _dispatcher.Post(() => ApplyExit(terminalId));
    }

    /// <summary>
    /// Снимает маркер умершей вкладки и забывает о ней. Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Вкладка остаётся на экране (раздел 5.1 ТЗ), но состояние её сессии больше не значит
    /// ничего: <c>SessionEnd</c> от убитой оболочки не придёт, а нового хода не будет.
    /// Забыть о вкладке обязательно, а не только показать <c>Unknown</c>: опоздавший
    /// <c>SubagentStop</c> увидел бы запомненную «фоновую работу», а опоздавший
    /// <c>PostToolBatch</c> — живую сессию, и зажгли бы на мёртвой вкладке точку «работает»
    /// до конца сеанса (разделы 5.3 и 8 ТЗ).
    /// </remarks>
    private void ApplyExit(TerminalId terminalId)
    {
        if (_disposed || _sink is not { } sink)
        {
            return;
        }

        sink.SetState(terminalId, TabState.Unknown);
        _sessions.Remove(terminalId);
        _activity.Remove(terminalId);
    }

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

        // Имя закончившейся сессии снимается раньше, чем показывается состояние новой.
        if (hookEvent.Kind == HookKind.SessionStart)
        {
            ResetTitleIfSessionChanged(sink, terminalId, hookEvent);
        }

        // Хуки сабагента несут cwd его собственного worktree: взять их — значит показать diff
        // той копии, что отчиталась последней, а не той, где работает главный агент.
        if (hookEvent.AgentId is null)
        {
            bool? sessionEnded = hookEvent.Kind switch
            {
                HookKind.SessionStart => false,
                HookKind.SessionEnd => true,
                _ => null,
            };
            sink.SetSessionContext(terminalId, hookEvent.SessionId, hookEvent.WorkingDirectory, sessionEnded);
        }

        if (!_activity.TryGetValue(terminalId, out var activity))
        {
            activity = new TabActivity();
            _activity[terminalId] = activity;
        }

        // Мимо TabActivity состояние не выставляется нигде — иначе запомненное разошлось бы
        // с показанным, а на нём держатся решения об опоздавших хуках.
        if (activity.Apply(hookEvent) is { } state)
        {
            sink.SetState(terminalId, state);
        }

        // Хуки сабагента к имени сессии отношения не имеют.
        if (hookEvent.AgentId is not null)
        {
            return;
        }

        switch (hookEvent.Kind)
        {
            case HookKind.SessionStart when HookSourceRules.StartsNewTurn(hookEvent.Source):
                // Сжатие контекста имени не меняет: транскрипт той же сессии, ход продолжается.
                if (Track(sink, terminalId, hookEvent) is { } started)
                {
                    started.Ended = false;
                    RequestTitle(sink, terminalId, started);
                }

                break;

            case HookKind.UserPromptSubmit:
                if (Track(sink, terminalId, hookEvent) is { } prompted)
                {
                    TitleFromPrompt(sink, terminalId, prompted, hookEvent.Prompt);

                    // Промпт в транскрипт ещё не лёг, но /rename между ходами — уже да.
                    RequestTitle(sink, terminalId, prompted);
                }

                break;

            case HookKind.Stop:
            case HookKind.StopFailure:
                // Конец хода — момент, когда транскрипт заведомо подрос: имя от Claude Code
                // (ai-title) появляется по ходу первого хода, а /rename — в любой момент. Оборванный
                // ход считается наравне: промпт в файл уже лёг. Это событие, а не таймер:
                // периодического опроса здесь нет и быть не может (запрет раздела 7 CLAUDE.md).
                if (Track(sink, terminalId, hookEvent) is { } ended)
                {
                    RequestTitle(sink, terminalId, ended);
                }

                break;

            case HookKind.SessionEnd:
                // Запись не удаляется: следующий SessionStart (после /clear — с новым session_id)
                // сверяется с ней, чтобы снять имя закончившейся сессии.
                if (_sessions.TryGetValue(terminalId, out var probe)
                    && (hookEvent.SessionId is null || hookEvent.SessionId == probe.SessionId))
                {
                    probe.Ended = true;
                }

                break;

            default:
                // Остальные хуки заголовка не касаются.
                break;
        }
    }

    /// <summary>
    /// Возвращает короткое имя к «новая сессия», если во вкладке началась именно другая сессия.
    /// Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Короткое имя принадлежит сессии, а не вкладке (раздел 6.3 ТЗ), поэтому имя закончившейся
    /// сессии висеть на экране не должно. Но сброс обязан быть условным: <c>SessionStart</c>
    /// приходит и на <c>--resume</c>, и на сжатие контекста, а при неизменившемся
    /// <c>session_id</c> заголовок мигал бы «новая сессия» и обратно на каждом сжатии. Сигнал
    /// смены — непустой и отличающийся идентификатор сессии: пустой означает «неизвестно»,
    /// а по незнанию имя не трогаем. Второй сигнал — <c>source: clear</c>: <c>/clear</c> всегда
    /// начинает новый разговор, даже если прежняя сессия вкладке не была известна.
    /// </remarks>
    private void ResetTitleIfSessionChanged(ITabStateSink sink, TerminalId terminalId, HookEvent hookEvent)
    {
        _sessions.TryGetValue(terminalId, out var probe);
        var cleared = string.Equals(hookEvent.Source, ClearSource, StringComparison.Ordinal);
        var changed = probe is not null
                      && !string.IsNullOrWhiteSpace(hookEvent.SessionId)
                      && hookEvent.SessionId != probe.SessionId;

        if (!changed && !(cleared && probe?.SessionId != hookEvent.SessionId))
        {
            return;
        }

        // Прежняя запись снимается вместе с именем: заголовок новой сессии ищется заново,
        // а опоздавшее чтение прежней его уже не перебьёт — PostLookupResult сверяет запись.
        _sessions.Remove(terminalId);
        sink.ResetShortTitle(terminalId);
    }

    /// <summary>
    /// Находит или заводит запись о сессии вкладки и дополняет её тем, что принёс хук.
    /// Выполняется в потоке интерфейса: <see cref="ITabStateSink.TryGetWorkingDirectory" /> — его член.
    /// </summary>
    /// <returns><c>null</c> — транскрипт искать не по чему: нет ни session_id, ни каталога.</returns>
    private SessionProbe? Track(ITabStateSink sink, TerminalId terminalId, HookEvent hookEvent)
    {
        _sessions.TryGetValue(terminalId, out var probe);

        // Значение самого события важнее запомненного: после --resume у вкладки другая сессия.
        var sessionId = FirstFilled(hookEvent.SessionId, probe?.SessionId);
        if (sessionId is null)
        {
            // Без session_id транскрипт не найти. Заголовок остаётся «новая сессия».
            return null;
        }

        var sameSession = probe is not null && probe.SessionId == sessionId;

        // Каталог: сначала cwd из хука, потом запомненный, потом каталог самой вкладки.
        var workingDirectory = FirstFilled(hookEvent.WorkingDirectory, sameSession ? probe!.WorkingDirectory : null);
        if (workingDirectory is null
            && sink.TryGetWorkingDirectory(terminalId, out var tabDirectory)
            && !string.IsNullOrWhiteSpace(tabDirectory))
        {
            workingDirectory = tabDirectory;
        }

        if (workingDirectory is null)
        {
            // Вкладки уже нет — событие опоздало.
            return null;
        }

        if (!sameSession)
        {
            // Новая сессия во вкладке. Промпт даёт ей имя, только если она начата с нуля: у
            // поднятой через --resume имя уже есть — в транскрипте и в сохранённой раскладке.
            var fresh = hookEvent.Kind == HookKind.SessionStart
                        && (string.Equals(hookEvent.Source, StartupSource, StringComparison.Ordinal)
                            || string.Equals(hookEvent.Source, ClearSource, StringComparison.Ordinal));
            probe = new SessionProbe(sessionId, fresh);
            _sessions[terminalId] = probe;
        }

        probe!.WorkingDirectory = workingDirectory;
        if (!string.IsNullOrWhiteSpace(hookEvent.TranscriptPath))
        {
            probe.TranscriptPath = hookEvent.TranscriptPath;
        }

        return probe;
    }

    /// <summary>
    /// Называет новую сессию по первому промпту сразу, не дожидаясь, пока имя появится
    /// в транскрипте. Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Без этого вкладка весь первый ход висела бы с «новая сессия»: имя от Claude Code
    /// (<c>ai-title</c>) и само сообщение ложатся в файл по ходу хода, а читается он на его
    /// конце. Имя из промпта — то же, что дал бы транскрипт без <c>ai-title</c>, и будет
    /// заменено им, как только оно появится.
    /// </remarks>
    private static void TitleFromPrompt(ITabStateSink sink, TerminalId terminalId, SessionProbe probe, string? prompt)
    {
        if (!probe.Fresh || probe.Title is not null || probe.Ended)
        {
            return;
        }

        if (SessionShortTitle.FromPrompt(prompt) is { } title)
        {
            probe.Title = title;
            sink.SetShortTitle(terminalId, title);
        }
    }

    /// <summary>
    /// Ставит чтение транскрипта ради имени вкладки (раздел 6.3 ТЗ). Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Бюджета попыток нет: имя сессии меняется всю её жизнь, а чтение дешёвое и ограниченное —
    /// неизменившийся файл отдаётся из кэша порта, дописанный стоит просмотра дописанного.
    /// Вызывается только на границах хода, то есть не чаще пары раз за ход. Промах (файла ещё
    /// нет) ничего не тратит: следующая граница хода просто спросит снова.
    /// <para>
    /// Одновременно идёт не больше одного чтения на вкладку. Запрос во время чтения не
    /// теряется: оно помечается как устаревшее и после завершения повторяется один раз —
    /// иначе <c>Stop</c>, пришедший, пока читался транскрипт по <c>UserPromptSubmit</c>,
    /// остался бы без чтения, и имя первого хода доехало бы только ходом позже.
    /// </para>
    /// </remarks>
    private void RequestTitle(ITabStateSink sink, TerminalId terminalId, SessionProbe probe)
    {
        if (probe.Ended)
        {
            return;
        }

        if (probe.InFlight)
        {
            probe.Rerun = true;
            return;
        }

        probe.InFlight = true;
        probe.Rerun = false;

        var load = LoadTitleAsync(sink, terminalId, probe, probe.SessionId, probe.TranscriptPath, probe.WorkingDirectory!);
        PendingTitleWork = PendingTitleWork.IsCompleted ? load : Task.WhenAll(PendingTitleWork, load);
    }

    /// <summary>
    /// Читает транскрипт и отдаёт вкладке имя сессии: данное Claude Code, иначе первое сообщение.
    /// </summary>
    /// <param name="sink">Полоса вкладок на момент запроса.</param>
    /// <param name="terminalId">Вкладка, которой нужен заголовок.</param>
    /// <param name="probe">Запись, для которой поставлено чтение.</param>
    /// <param name="sessionId">Сессия, чей транскрипт читается.</param>
    /// <param name="transcriptPath">Путь из <c>transcript_path</c>; <c>null</c> — не приходил.</param>
    /// <param name="workingDirectory">Каталог, по которому транскрипт ищется, если путь не годится.</param>
    /// <remarks>
    /// Метод не бросает: заголовок вкладки — не повод ронять приложение, а формат <c>.jsonl</c>
    /// считается нестабильным (раздел 7 CLAUDE.md).
    /// </remarks>
    private async Task LoadTitleAsync(
        ITabStateSink sink,
        TerminalId terminalId,
        SessionProbe probe,
        string sessionId,
        string? transcriptPath,
        string workingDirectory)
    {
        // Уступаем поток интерфейса прежде, чем трогать порт: заголовок не стоит ни кадра
        // вывода терминала.
        await Task.Yield();

        string? title;
        try
        {
            // Task.Run, а не голый await: до первого настоящего await порт успевает сделать
            // FileInfo.Exists и открыть FileStream, а открытие файла синхронно даже
            // с useAsync: true. Под антивирусом CreateFile блокируется на десятки миллисекунд,
            // и держать их в потоке интерфейса нельзя — хуки идут с приоритетом Normal, то есть
            // впереди очереди кадров вывода терминала. Одного Task.Yield тут мало: при
            // установленном SynchronizationContext он возвращает продолжение в тот же поток.
            var summary = await Task.Run(
                async () =>
                {
                    // Путь из хука точнее: после cd или перехода в worktree каталог, собранный
                    // из cwd, уже не тот. Не годится или файла по нему нет — прежний путь через slug.
                    var byPath = transcriptPath is null
                        ? null
                        : await _history.ReadTranscriptAsync(transcriptPath, sessionId, _lifetimeToken).ConfigureAwait(false);
                    return byPath ?? await _history.ReadOneAsync(workingDirectory, sessionId, _lifetimeToken).ConfigureAwait(false);
                },
                _lifetimeToken).ConfigureAwait(false);
            title = SessionShortTitle.Shorten(summary?.DisplayTitle);
        }
        catch (Exception)
        {
            // Окно закрылось посреди чтения, файл исчез, разбор сорвался — заголовок просто
            // не обновится, и это не ошибка. Следующая граница хода спросит снова.
            title = null;
        }

        PostLookupResult(sink, terminalId, probe, title);
    }

    /// <summary>Возвращает исход чтения в поток интерфейса.</summary>
    private void PostLookupResult(ITabStateSink sink, TerminalId terminalId, SessionProbe probe, string? title)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_sink, sink))
            {
                return;
            }

            // Пока читали, вкладка могла начать другую сессию — тогда результат уже чужой.
            if (!_sessions.TryGetValue(terminalId, out var current) || !ReferenceEquals(current, probe))
            {
                return;
            }

            probe.InFlight = false;

            // Пустой ответ имя не трогает: файла могло ещё не быть, а имя из промпта уже стоит.
            if (title is not null && title != probe.Title)
            {
                probe.Title = title;
                sink.SetShortTitle(terminalId, title);
            }

            if (probe.Rerun)
            {
                RequestTitle(sink, terminalId, probe);
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

    /// <summary>Сессия вкладки: где лежит её транскрипт и что уже показано.</summary>
    /// <remarks>
    /// Запись живёт от первого хука сессии до смены сессии во вкладке или смерти процесса;
    /// <c>SessionEnd</c> её не удаляет, а помечает (<see cref="Ended"/>) — иначе
    /// <c>SessionStart</c> после <c>/clear</c> не с чем было бы сверить. Вкладка, закрытая
    /// пользователем, оставляет запись до выхода из приложения: это несколько десятков байт
    /// на вкладку за сеанс, и ради них не стоит заводить ещё одну подписку на жизненный цикл вкладок.
    /// Трогается только из потока интерфейса.
    /// </remarks>
    private sealed class SessionProbe(string sessionId, bool fresh)
    {
        /// <summary>Идентификатор сессии Claude Code из хука.</summary>
        public string SessionId { get; } = sessionId;

        /// <summary>
        /// Сессия начата с нуля (<c>startup</c>, <c>clear</c>): первый промпт может сразу дать ей имя.
        /// </summary>
        public bool Fresh { get; } = fresh;

        /// <summary>Каталог, по которому искать транскрипт, если путь из хука не годится.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Последний <c>transcript_path</c> из хуков сессии.</summary>
        public string? TranscriptPath { get; set; }

        /// <summary>Последнее имя, выставленное вкладке этой сессией.</summary>
        public string? Title { get; set; }

        /// <summary>Пришёл <c>SessionEnd</c>: транскрипт больше не читается.</summary>
        public bool Ended { get; set; }

        /// <summary>Транскрипт читается прямо сейчас — второе чтение не ставим.</summary>
        public bool InFlight { get; set; }

        /// <summary>Во время чтения пришла ещё одна граница хода — прочитать ещё раз после.</summary>
        public bool Rerun { get; set; }
    }
}
