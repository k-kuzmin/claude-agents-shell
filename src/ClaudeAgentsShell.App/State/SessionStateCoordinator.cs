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
    /// <summary>
    /// Сколько раз за сессию читается транскрипт в поисках короткого имени, считая попытку
    /// по <c>SessionStart</c>. Исчерпан бюджет — вкладка до конца сессии остаётся
    /// с «новая сессия».
    /// </summary>
    /// <remarks>
    /// Верхняя граница нужна потому, что повтор висит на <c>Stop</c>, то есть на каждом ответе
    /// агента, а транскрипт растёт весь сеанс. Нижняя — потому что заголовок может появиться
    /// не с первого хода: если пользователь начал со слэш-команды, её строка обёрнута
    /// в <c>&lt;command-name&gt;</c> и заголовком не становится, а настоящий запрос приходит
    /// следующим сообщением. Четырёх попыток хватает на такое начало с запасом, и стоят они
    /// не больше четырёх чтений в потоке пула за всю сессию.
    /// </remarks>
    private const int MaxTitleAttempts = 4;

    private readonly IHookListener _hooks;
    private readonly ITerminalWorkspace _workspace;
    private readonly ISessionHistoryReader _history;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>
    /// Чем читать транскрипт вкладки и нужен ли ей ещё заголовок. Трогается только из потока
    /// интерфейса, поэтому обычный словарь без блокировок.
    /// </summary>
    private readonly Dictionary<TerminalId, SessionProbe> _sessions = [];

    /// <summary>
    /// Что известно о вкладке прямо сейчас: сколько у неё незавершённых сабагентов и какое
    /// состояние ей выставлено последним. Трогается только из потока интерфейса, поэтому
    /// обычный словарь без блокировок.
    /// </summary>
    /// <remarks>
    /// Счётчик сабагентов приложение ведёт само: ни <c>SubagentStart</c>, ни <c>SubagentStop</c>
    /// не сообщают, сколько их осталось. Хук идёт через <c>curl</c> и может потеряться, поэтому
    /// счётчик не только вычитается, но и **обнуляется на границах хода** — на <c>SessionStart</c>
    /// и на <c>UserPromptSubmit</c>. Так потерянный <c>SubagentStop</c> живёт не дольше одного
    /// хода, а не залипает вкладкой в <see cref="TabState.BackgroundWork"/> навсегда.
    /// В минус счётчик не уходит: лишний <c>SubagentStop</c> — это тот же дрейф, только в другую
    /// сторону, и отрицательный остаток скрыл бы следующий настоящий запуск сабагента.
    /// <para>
    /// Покрыты только сабагенты. Фоновые команды <c>Bash</c> с <c>run_in_background</c>
    /// не покрыты вовсе: хуков для них у Claude Code нет — ни на запуск, ни на завершение.
    /// Сессия, ждущая только фоновую команду, покажет «ждёт ввода», и это не ошибка счётчика,
    /// а отсутствие источника события.
    /// </para>
    /// <para>
    /// Последнее состояние хранится здесь, а не спрашивается у <see cref="ITabStateSink"/>:
    /// порт остаётся узким, а координатору оно нужно ровно в одном месте — чтобы
    /// <c>SubagentStop</c>, опоздавший к уже выставленному «ждёт ввода», не сбил маркер.
    /// </para>
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
    /// <c>SubagentStart</c>, <c>SubagentStop</c>, <c>SessionEnd</c>.
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
                ResetTitleIfSessionChanged(sink, terminalId, hookEvent);

                // Начало сессии — граница хода: незакрытые сабагенты прежней сессии не считаются.
                ResetBackgroundAgents(terminalId);
                SetState(sink, terminalId, TabState.Idle);
                RequestTitle(sink, terminalId, hookEvent);
                break;

            case HookKind.UserPromptSubmit:
                // Тоже граница хода: всё, что осталось от прежнего хода, к новому отношения
                // не имеет, и потерянный SubagentStop дальше этой точки не живёт.
                ResetBackgroundAgents(terminalId);
                SetState(sink, terminalId, TabState.Busy);
                break;

            case HookKind.Stop:
                // Ход агента кончился. Если сабагенты ещё работают, от человека сейчас ничего
                // не нужно: сессия продолжится сама, когда они закончат.
                SetState(
                    sink,
                    terminalId,
                    BackgroundAgents(terminalId) > 0 ? TabState.BackgroundWork : TabState.AwaitingInput);

                // Ещё одна попытка достать заголовок. Ответ агента — единственный момент, когда
                // транскрипт заведомо подрос, поэтому повтор привязан к нему. Это событие,
                // а не таймер: периодического опроса здесь нет и быть не может (запрет раздела 7
                // CLAUDE.md), а число попыток ограничено (см. MaxTitleAttempts).
                RequestTitle(sink, terminalId, hookEvent);
                break;

            case HookKind.SubagentStart:
                // Только счётчик: состояние вкладки уже «работает» — сабагента запускает
                // работающий агент.
                ChangeBackgroundAgents(terminalId, delta: 1);
                break;

            case HookKind.SubagentStop:
                // Последний сабагент закончил — главный агент вот-вот продолжит сам.
                // Проверка состояния обязательна: SubagentStop, опоздавший или пришедший дважды
                // уже после «ждёт ввода», иначе сбил бы маркер, которого ждёт пользователь.
                if (ChangeBackgroundAgents(terminalId, delta: -1) == 0
                    && StateOf(terminalId) == TabState.BackgroundWork)
                {
                    SetState(sink, terminalId, TabState.Busy);
                }

                break;

            case HookKind.SessionEnd:
                _sessions.Remove(terminalId);

                // Сессии больше нет — её сабагентов тоже: счётчик снимается вместе с маркером.
                ResetBackgroundAgents(terminalId);
                SetState(sink, terminalId, TabState.Unknown);
                break;

            case HookKind.Unknown:
            default:
                // Незарегистрированный хук игнорируется: состояние вкладки не меняется.
                break;
        }
    }

    /// <summary>
    /// Единственный путь, которым выставляется состояние вкладки: отдаёт его полосе вкладок
    /// и запоминает. Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Мимо этого метода состояние не выставляется нигде — иначе запомненное разошлось бы
    /// с показанным, а на нём держится решение <c>SubagentStop</c>.
    /// </remarks>
    private void SetState(ITabStateSink sink, TerminalId terminalId, TabState state)
    {
        _activity.TryGetValue(terminalId, out var activity);
        _activity[terminalId] = activity with { State = state };
        sink.SetState(terminalId, state);
    }

    /// <summary>Состояние, выставленное вкладке последним; о незнакомой вкладке — <c>Unknown</c>.</summary>
    private TabState StateOf(TerminalId terminalId) =>
        _activity.TryGetValue(terminalId, out var activity) ? activity.State : TabState.Unknown;

    /// <summary>Сколько сабагентов вкладки сейчас считаются незавершёнными.</summary>
    private int BackgroundAgents(TerminalId terminalId) =>
        _activity.TryGetValue(terminalId, out var activity) ? activity.BackgroundAgents : 0;

    /// <summary>Меняет счётчик сабагентов и возвращает новое значение. Ниже нуля не опускается.</summary>
    private int ChangeBackgroundAgents(TerminalId terminalId, int delta)
    {
        _activity.TryGetValue(terminalId, out var activity);
        var count = Math.Max(0, activity.BackgroundAgents + delta);
        _activity[terminalId] = activity with { BackgroundAgents = count };
        return count;
    }

    /// <summary>Снимает счётчик сабагентов на границе хода — см. <see cref="_activity"/>.</summary>
    private void ResetBackgroundAgents(TerminalId terminalId)
    {
        _activity.TryGetValue(terminalId, out var activity);
        _activity[terminalId] = activity with { BackgroundAgents = 0 };
    }

    /// <summary>
    /// Возвращает короткое имя к «новая сессия», если во вкладке началась именно другая сессия.
    /// Выполняется в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Короткое имя принадлежит сессии, а не вкладке (раздел 6.3 ТЗ), поэтому имя закончившейся
    /// сессии висеть на экране не должно. Но сброс обязан быть условным: <c>SessionStart</c>
    /// приходит и на <c>--resume</c>, и на <c>/clear</c>, и на сжатие контекста, а при
    /// неизменившемся <c>session_id</c> заголовок мигал бы «новая сессия» и обратно на каждом
    /// сжатии. Сигнал смены — только непустой и отличающийся идентификатор сессии: пустой
    /// означает «неизвестно», а по незнанию имя не трогаем.
    /// </remarks>
    private void ResetTitleIfSessionChanged(ITabStateSink sink, TerminalId terminalId, HookEvent hookEvent)
    {
        if (!_sessions.TryGetValue(terminalId, out var probe))
        {
            // О прежней сессии вкладки ничего не известно — сбрасывать нечего.
            return;
        }

        if (string.IsNullOrWhiteSpace(hookEvent.SessionId) || hookEvent.SessionId == probe.SessionId)
        {
            return;
        }

        // Прежняя запись снимается вместе с именем: заголовок новой сессии ищется заново,
        // а опоздавшее чтение прежней его уже не перебьёт — LoadTitleAsync сверяет session_id.
        _sessions.Remove(terminalId);
        sink.ResetShortTitle(terminalId);
    }

    /// <summary>
    /// Ставит чтение транскрипта ради короткого имени вкладки (раздел 6.3 ТЗ), если оно ещё нужно.
    /// Выполняется в потоке интерфейса: <see cref="ITabStateSink.TryGetWorkingDirectory" /> — его член.
    /// </summary>
    private void RequestTitle(ITabStateSink sink, TerminalId terminalId, HookEvent hookEvent)
    {
        _sessions.TryGetValue(terminalId, out var probe);

        // Значение самого события важнее запомненного: после --resume у вкладки другая сессия.
        var sessionId = FirstFilled(hookEvent.SessionId, probe?.SessionId);
        if (sessionId is null)
        {
            // Без session_id транскрипт не найти. Заголовок остаётся «новая сессия».
            return;
        }

        // Стадия поиска относится к конкретной сессии, поэтому сверяется вместе с ней. Иначе
        // вкладка, у которой SessionStart потерялся или пришёл без session_id, так и осталась бы
        // с Resolved от закончившейся сессии — и чтение для новой не поставилось бы вовсе.
        var sameSession = probe is not null && probe.SessionId == sessionId;
        if (sameSession && probe!.Lookup is not TitleLookup.Pending)
        {
            // Имя уже есть, попытки исчерпаны или чтение идёт прямо сейчас. Без этой проверки
            // каждый Stop запускал бы новый проход по растущему файлу, и проходы накладывались бы.
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

        // Счётчик попыток принадлежит сессии: у новой он начинается заново.
        var attempts = sameSession ? probe!.Attempts : 0;
        _sessions[terminalId] = new SessionProbe(sessionId, workingDirectory, TitleLookup.InFlight, attempts);

        var load = LoadTitleAsync(sink, terminalId, sessionId, workingDirectory);
        PendingTitleWork = PendingTitleWork.IsCompleted ? load : Task.WhenAll(PendingTitleWork, load);
    }

    /// <summary>
    /// Читает транскрипт и, если из первого сообщения вышло короткое имя, отдаёт его вкладке.
    /// </summary>
    /// <param name="sink">Полоса вкладок на момент запроса.</param>
    /// <param name="terminalId">Вкладка, которой нужен заголовок.</param>
    /// <param name="sessionId">Сессия, чей транскрипт читается.</param>
    /// <param name="workingDirectory">Каталог, в котором лежит транскрипт.</param>
    /// <remarks>
    /// Метод не бросает: заголовок вкладки — не повод ронять приложение, а формат <c>.jsonl</c>
    /// считается нестабильным (раздел 7 CLAUDE.md).
    /// </remarks>
    private async Task LoadTitleAsync(
        ITabStateSink sink,
        TerminalId terminalId,
        string sessionId,
        string workingDirectory)
    {
        // Уступаем поток интерфейса прежде, чем трогать порт: заголовок не стоит ни кадра
        // вывода терминала.
        await Task.Yield();

        SessionSummary? summary;
        try
        {
            // Task.Run, а не голый await: до первого настоящего await порт успевает сделать
            // FileInfo.Exists и открыть FileStream, а открытие файла синхронно даже
            // с useAsync: true. Под антивирусом CreateFile блокируется на десятки миллисекунд,
            // и держать их в потоке интерфейса нельзя — хуки идут с приоритетом Normal, то есть
            // впереди очереди кадров вывода терминала. Одного Task.Yield тут мало: при
            // установленном SynchronizationContext он возвращает продолжение в тот же поток.
            summary = await Task.Run(
                () => _history.ReadOneAsync(workingDirectory, sessionId, _lifetimeToken),
                _lifetimeToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Окно закрылось посреди чтения, файл исчез, разбор сорвался — заголовок просто
            // не обновится, и это не ошибка. Вкладка остаётся открытой для следующей попытки.
            PostLookupResult(sink, terminalId, sessionId, shortTitle: null);
            return;
        }

        PostLookupResult(sink, terminalId, sessionId, SessionShortTitle.Shorten(summary?.Title));
    }

    /// <summary>
    /// Возвращает исход поиска заголовка в поток интерфейса и решает, будет ли ещё попытка.
    /// </summary>
    private void PostLookupResult(ITabStateSink sink, TerminalId terminalId, string sessionId, string? shortTitle)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_sink, sink))
            {
                return;
            }

            // Пока читали, вкладка могла начать другую сессию — тогда результат уже чужой.
            if (!_sessions.TryGetValue(terminalId, out var probe) || probe.SessionId != sessionId)
            {
                return;
            }

            var attempts = probe.Attempts + 1;

            // Отсутствие заголовка не доказывает, что его не будет: первым ходом пользователя
            // бывает слэш-команда (/init, /review, промпт MCP), а её строка приходит обёрткой
            // <command-name>, которую очистка отбрасывает. Настоящий запрос ляжет в транскрипт
            // следующим сообщением, и заголовок появится. Поэтому попытки не обрываются на первом
            // пустом ответе, а просто считаются: бюджет исчерпан — перестаём спрашивать.
            var lookup = shortTitle is not null
                ? TitleLookup.Resolved
                : attempts >= MaxTitleAttempts
                    ? TitleLookup.Unavailable
                    : TitleLookup.Pending;

            _sessions[terminalId] = probe with { Lookup = lookup, Attempts = attempts };

            if (shortTitle is not null)
            {
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

    /// <summary>Что известно о вкладке прямо сейчас — см. <see cref="_activity"/>.</summary>
    /// <param name="BackgroundAgents">Сколько сабагентов вкладки считаются незавершёнными.</param>
    /// <param name="State">Состояние, выставленное вкладке последним.</param>
    private readonly record struct TabActivity(int BackgroundAgents, TabState State);

    /// <summary>Чем читать транскрипт вкладки и нужен ли ей ещё заголовок.</summary>
    /// <remarks>
    /// Запись живёт от <c>SessionStart</c> до <c>SessionEnd</c>. Вкладка, закрытая пользователем
    /// без <c>SessionEnd</c>, оставляет запись до выхода из приложения: это несколько десятков байт
    /// на вкладку за сеанс, и ради них не стоит заводить ещё одну подписку на жизненный цикл вкладок.
    /// </remarks>
    /// <param name="SessionId">Идентификатор сессии Claude Code из хука.</param>
    /// <param name="WorkingDirectory">Каталог, в котором искать транскрипт.</param>
    /// <param name="Lookup">На какой стадии поиск короткого имени.</param>
    /// <param name="Attempts">Сколько чтений транскрипта этой сессии уже завершилось.</param>
    private sealed record SessionProbe(
        string SessionId,
        string WorkingDirectory,
        TitleLookup Lookup,
        int Attempts);

    /// <summary>Стадия поиска короткого имени сессии.</summary>
    /// <remarks>
    /// Новое чтение ставится только из <see cref="Pending"/>. Так транскрипт читается не чаще
    /// <see cref="MaxTitleAttempts"/> раз за сессию, а не на каждый ответ агента.
    /// </remarks>
    private enum TitleLookup
    {
        /// <summary>Заголовка нет, но попытка ещё осмысленна.</summary>
        Pending = 0,

        /// <summary>Транскрипт читается прямо сейчас — второе чтение не ставим.</summary>
        InFlight = 1,

        /// <summary>Короткое имя выставлено.</summary>
        Resolved = 2,

        /// <summary>Бюджет попыток исчерпан — больше не ищем.</summary>
        Unavailable = 3,
    }
}
