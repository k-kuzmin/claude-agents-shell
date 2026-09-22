using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Output;

namespace ClaudeAgentsShell.Terminal;

/// <summary>
/// Связывает мост и псевдоконсоли: маршрутизирует сообщения страницы по идентификатору вкладки
/// и держит помпу на каждую открытую вкладку. Страница рассчитана на N терминалов в одном
/// WebView2 (раздел 3.1 ТЗ), поэтому вкладки различаются только идентификатором.
/// </summary>
public sealed class TerminalWorkspace : ITerminalWorkspace
{
    private readonly ITerminalBridge _bridge;
    private readonly IPtySessionFactory _ptyFactory;
    private readonly IShellResolver _shells;
    private readonly ISessionCommandBuilder _commands;
    private readonly IHookSettingsProvider _hooks;
    private readonly IHookListener _hookListener;
    private readonly IMcpConfigProvider _mcpConfig;
    private readonly TerminalOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TerminalPump> _pumps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingTerminal> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _background = new();

    /// <summary>
    /// Токен вкладки → вкладка. Направление именно такое: по токену из хука вкладка ищется
    /// на каждом событии, а обратный поиск нужен только при закрытии — там хватает перебора.
    /// </summary>
    private readonly ConcurrentDictionary<string, TerminalId> _tokens = new(StringComparer.Ordinal);

    private readonly List<TerminalId> _order = [];
    private readonly object _orderSync = new();
    private readonly object _hookSync = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Код выхода для вкладки, чья псевдоконсоль так и не поднялась. Настоящего кода нет —
    /// процесса не было, — а ноль означал бы штатное завершение.
    /// </summary>
    private const int StartFailureExitCode = -1;

    private Task<SessionIntegration>? _integration;
    private int _disposed;

    /// <inheritdoc cref="TerminalWorkspace" />
    public TerminalWorkspace(
        ITerminalBridge bridge,
        IPtySessionFactory ptyFactory,
        IShellResolver shells,
        ISessionCommandBuilder commands,
        IHookSettingsProvider hooks,
        IHookListener hookListener,
        IMcpConfigProvider mcpConfig,
        TerminalOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(ptyFactory);
        ArgumentNullException.ThrowIfNull(shells);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(hookListener);
        ArgumentNullException.ThrowIfNull(mcpConfig);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _bridge = bridge;
        _ptyFactory = ptyFactory;
        _shells = shells;
        _commands = commands;
        _hooks = hooks;
        _hookListener = hookListener;
        _mcpConfig = mcpConfig;
        _options = options;
        _timeProvider = timeProvider;

        _bridge.TerminalReady += OnTerminalReady;
        _bridge.InputReceived += OnInputReceived;
        _bridge.ResizeRequested += OnResizeRequested;
    }

    /// <summary>
    /// Процесс вкладки завершился. Событие приходит с потока, на котором ОС сообщила о выходе
    /// процесса, — подписчик обязан сам уйти в свой поток (для WPF это <c>Dispatcher</c>).
    /// </summary>
    public event EventHandler<TerminalExitedEventArgs>? TerminalExited;

    /// <inheritdoc />
    public IReadOnlyList<TerminalId> Terminals
    {
        get
        {
            lock (_orderSync)
            {
                return _order.ToArray();
            }
        }
    }

    /// <summary>
    /// Поднимает страницу терминалов. Вкладки открывает прикладной код: какие проекты
    /// восстанавливать при старте, слой терминала не решает.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await _bridge.InitializeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Открывает вкладку проекта: просит страницу создать терминал, поднимает оболочку
    /// в рабочем каталоге проекта и пишет в её stdin команду запуска.
    /// <para>
    /// Открытая вкладка сразу становится видимой: страница показывает ровно один терминал,
    /// и вкладка, которой никто не показал, осталась бы чёрным окном.
    /// </para>
    /// </summary>
    public async Task<TerminalId> OpenAsync(
        ProjectDefinition project,
        SessionLaunch launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launch);

        // Оболочка разрешается и команда собирается до того, как вкладка попала в учёт:
        // оба вызова бросают, и полузарегистрированный идентификатор остался бы в Terminals
        // навсегда — без псевдоконсоли и без терминала на странице.
        var shell = _shells.Resolve(project.Shell);

        // Файлы интеграции готовятся до сборки команды: их пути уходят в команду запуска
        // аргументами --settings и --mcp-config (раздел 5.3 ТЗ, issue #5). Какой не вышел —
        // null, и построитель собирает команду без него: сессия работает, просто без маркера
        // состояния или без инструмента show_diff.
        var integration = await EnsureIntegrationAsync().ConfigureAwait(false);

        var startupInput = _commands.Build(project, launch, integration);

        var terminalId = TerminalId.New();

        // Токен вкладки. Уезжает в окружение псевдоконсоли, оттуда его берёт команда хука
        // и возвращает в HookEvent.CorrelationToken — так событие сопоставляется со вкладкой,
        // не полагаясь на совпадение рабочих каталогов. Токен криптостойкий, а не порядковый:
        // приёмник хуков слушает обычный HTTP на loopback, и по угаданному токену любой
        // локальный процесс переключал бы состояние чужой вкладки.
        string token = NewCorrelationToken();

        // Окружение процесса наследуется от приложения; здесь только то, что добавляется
        // поверх. TERM обязателен — без него TUI рисует рамки псевдографикой. Переменные хуков
        // (токен вкладки и то, что нужно их транспорту) отдаёт слой хуков.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM"] = "xterm-256color",
        };
        foreach (var (name, value) in _hooks.SessionEnvironment(token))
        {
            environment[name] = value;
        }

        var startInfo = new PtyStartInfo(shell, project.Path, TerminalSize.Default, environment);

        _pending[terminalId.Value] = new PendingTerminal(startInfo, startupInput);
        _tokens[token] = terminalId;
        Register(terminalId);

        try
        {
            await _bridge.CreateTerminalAsync(terminalId, Title(project), cancellationToken)
                .ConfigureAwait(false);
            await _bridge.ShowTerminalAsync(terminalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Страница вкладку не приняла. Псевдоконсоли ещё нет — она поднимается по ready,
            // — поэтому освобождать нечего, достаточно убрать вкладку из учёта.
            _pending.TryRemove(terminalId.Value, out _);
            _tokens.TryRemove(token, out _);
            Unregister(terminalId);
            throw;
        }

        return terminalId;
    }

    /// <summary>
    /// Делает вкладку видимой — ровно одно сообщение <c>show</c> на страницу.
    /// Ни пересоздания терминала, ни перерисовки, ни ресайза: все вкладки страницы держатся
    /// одного размера, поэтому показ ничего не пересчитывает (раздел 7 ТЗ, критерий 2).
    /// </summary>
    public async Task ActivateAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        if (!Contains(terminalId))
        {
            // Неизвестный идентификатор спрятал бы на странице все терминалы разом:
            // show скрывает всё, кроме указанного.
            return;
        }

        await _bridge.ShowTerminalAsync(terminalId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId)
    {
        if (string.IsNullOrEmpty(correlationToken))
        {
            terminalId = default;
            return false;
        }

        return _tokens.TryGetValue(correlationToken, out terminalId);
    }

    /// <summary>
    /// Закрывает вкладку: гасит псевдоконсоль, убирает помпу из маршрутизации и просит
    /// страницу уничтожить терминал. Единственный путь удаления вкладки — здесь же
    /// снимаются ожидания записи, иначе они копились бы на каждой закрытой вкладке.
    /// </summary>
    public async Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        await CloseAsync(terminalId, notifyPage: true, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _bridge.TerminalReady -= OnTerminalReady;
        _bridge.InputReceived -= OnInputReceived;
        _bridge.ResizeRequested -= OnResizeRequested;

        await _cts.CancelAsync().ConfigureAwait(false);

        // Параллельно: у каждой вкладки свой бюджет ожидания выхода процесса, и последовательное
        // закрытие умножало бы его на число вкладок, держа окно на экране всё это время.
        await Task.WhenAll(_pumps.Keys
                .Select(id => CloseAsync(new TerminalId(id), notifyPage: false)))
            .ConfigureAwait(false);

        // Дожидаемся фоновых работ: гашений, начатых выходом оболочки (их вкладок в _pumps
        // уже нет), и записей команды запуска.
        try
        {
            await Task.WhenAll(_background.Keys).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Исход каждой такой работы уже наблюдён через Observe.
        }

        _pending.Clear();
        _tokens.Clear();

        lock (_orderSync)
        {
            _order.Clear();
        }

        _cts.Dispose();
    }

    private async Task CloseAsync(TerminalId terminalId, bool notifyPage, CancellationToken cancellationToken = default)
    {
        if (notifyPage)
        {
            // Вкладка уходит со страницы только по явному закрытию — значит, и из учёта она
            // уходит здесь же. Самостоятельно вышедшая оболочка вкладку не удаляет
            // (раздел 8 ТЗ), поэтому на том пути порядок не трогается.
            //
            // Вместе с вкладкой уходит и её токен: события хуков по нему больше никого
            // не найдут, а сам он не должен копиться до конца жизни приложения. Перебор
            // здесь дешевле второй карты: вкладок десятки, а закрытие — действие человека.
            foreach (var pair in _tokens)
            {
                if (pair.Value == terminalId)
                {
                    _tokens.TryRemove(pair.Key, out _);
                }
            }

            // Снятие с учёта идёт ДО снятия помпы. Обработчик ready может в этот момент
            // поднимать псевдоконсоль на другом потоке: он добавляет помпу, а потом сверяется
            // с учётом. Один из двух порядков обязательно увидит другой — иначе помпа,
            // добавленная сразу после поиска здесь, осталась бы жить без вкладки.
            Unregister(terminalId);
        }

        _pending.TryRemove(terminalId.Value, out _);

        if (_pumps.TryRemove(terminalId.Value, out var pump))
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }

        if (notifyPage)
        {
            await _bridge.CloseTerminalAsync(terminalId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Оболочка завершилась сама. Псевдоконсоль и помпу освобождаем сразу, а терминал
    /// на странице оставляем: пользователь должен увидеть код выхода (раздел 8 ТЗ).
    /// Выполняется не на потоке колбэка завершения процесса — освобождение сессии
    /// дожидается этого колбэка и на нём же заблокировалось бы.
    /// </summary>
    private void OnShellExited(TerminalId terminalId, int exitCode)
    {
        var closing = Task.Run(() => CloseAsync(terminalId, notifyPage: false));

        // Гашение учитывается: если оболочка вышла ровно в момент закрытия окна, помпа уже
        // убрана из _pumps, и DisposeAsync прошёл бы мимо — приложение завершилось бы, не
        // дождавшись закрытия псевдоконсоли.
        Track(closing);
        Observe(closing, "освобождение вкладки после выхода оболочки");

        if (!Contains(terminalId))
        {
            // Вкладку закрыл пользователь: закрытие псевдоконсоли гасит оболочку, и её выход —
            // следствие закрытия, а не событие для интерфейса. Оговорка: закрытие и выход
            // могут совпасть, поэтому подписчик обязан терпеть событие о неизвестной вкладке.
            return;
        }

        RaiseExited(terminalId, exitCode);
    }

    /// <summary>
    /// Сообщает, что под вкладкой больше нет живой псевдоконсоли, — по выходу оболочки или
    /// по несостоявшемуся подъёму. Вызов приходит с чужого потока (колбэк ОС о завершении
    /// процесса либо обработчик сообщения страницы), поэтому подписчик уходит в свой сам.
    /// </summary>
    private void RaiseExited(TerminalId terminalId, int exitCode)
    {
        try
        {
            TerminalExited?.Invoke(this, new TerminalExitedEventArgs(terminalId, exitCode));
        }
        catch (Exception exception)
        {
            // На пути выхода вызов приходит с колбэка ОС: исключение подписчика здесь
            // никто не поймает и оно снесёт процесс целиком.
            System.Diagnostics.Trace.TraceError(
                "Claude Agents Shell: обработчик TerminalExited бросил исключение: {0}",
                exception);
        }
    }

    private void Track(Task task)
    {
        _background[task] = 0;

        _ = task.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _background,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Register(TerminalId terminalId)
    {
        lock (_orderSync)
        {
            _order.Add(terminalId);
        }
    }

    private void Unregister(TerminalId terminalId)
    {
        lock (_orderSync)
        {
            _order.Remove(terminalId);
        }
    }

    private bool Contains(TerminalId terminalId)
    {
        lock (_orderSync)
        {
            return _order.Contains(terminalId);
        }
    }

    /// <summary>
    /// Освобождает помпу, не трогая страницу: терминал на ней принадлежит либо уже живущей
    /// вкладке, либо вкладке с пометкой о выходе, и <c>close</c> уничтожил бы его вместе
    /// с историей. Освобождение учитывается, иначе закрытие окна могло бы его не дождаться.
    /// </summary>
    private void ReleasePump(TerminalPump pump, string what)
    {
        var release = ReleaseAsync(pump);
        Track(release);
        Observe(release, what);

        static async Task ReleaseAsync(TerminalPump pump) =>
            await pump.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Заголовок терминала на странице — как в примере раздела 3.2 ТЗ: проект и его каталог.</summary>
    private static string Title(ProjectDefinition project) => $"{project.Name} · {project.Path}";

    /// <summary>
    /// Токен вкладки: 128 бит из криптографического источника. Угадать его должно быть
    /// нельзя — по нему приёмник хуков доверяет событию, а слушает он обычный HTTP
    /// на loopback, доступный любому локальному процессу.
    /// </summary>
    private static string NewCorrelationToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Готовит файлы интеграции: настройки хуков и конфиг MCP. Оба одни на приложение
    /// (см. <see cref="IHookSettingsProvider"/> и <see cref="IMcpConfigProvider"/>), поэтому
    /// создаются один раз на всё время жизни набора вкладок: переписывать их при открытии
    /// каждой вкладки значило бы менять файлы под уже запущенными сессиями.
    /// </summary>
    private Task<SessionIntegration> EnsureIntegrationAsync()
    {
        lock (_hookSync)
        {
            return _integration ??= CreateIntegrationAsync();
        }
    }

    private async Task<SessionIntegration> CreateIntegrationAsync()
    {
        // Половины деградируют независимо: хуки без MCP — вкладка с маркером, но без show_diff;
        // MCP без хуков — инструмент есть, но спросит разрешения (правило лежит в настройках хуков).
        var hookSettings = await TryCreateFileAsync(
            () => _hooks.EnsureSettingsFileAsync(_hookListener.Endpoint, _cts.Token)).ConfigureAwait(false);
        var mcpConfig = await TryCreateFileAsync(
            () => _mcpConfig.EnsureConfigFileAsync(_hookListener.McpEndpoint, _cts.Token)).ConfigureAwait(false);

        return new SessionIntegration(hookSettings, mcpConfig);
    }

    private static async Task<string?> TryCreateFileAsync(Func<Task<string>> create)
    {
        try
        {
            return await create().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or OperationCanceledException)
        {
            // Раздел 5.3 ТЗ: интеграция не встала — сессия запускается без этого флага.
            // Это допустимая деградация, ошибку пользователю не показываем.
            // Повторных попыток нет намеренно: результат кэшируется, иначе каждая новая
            // вкладка снова упиралась бы в тот же недоступный каталог.
            // InvalidOperationException ловится тоже: приёмник мог не подняться,
            // и тогда адреса для файла попросту нет.
            return null;
        }
    }

    private void OnTerminalReady(object? sender, TerminalReadyEventArgs args)
    {
        if (!_pending.TryRemove(args.TerminalId.Value, out var pending))
        {
            return;
        }

        try
        {
            // Псевдоконсоль поднимается синхронно, до возврата из обработчика: следом за ready
            // страница пришлёт resize, и помпа к этому моменту уже зарегистрирована.
            var session = _ptyFactory.Create(pending.StartInfo);
            var pump = new TerminalPump(args.TerminalId, session, _bridge, _options, _timeProvider);

            if (!_pumps.TryAdd(args.TerminalId.Value, pump))
            {
                // Вкладка с таким идентификатором уже жива: освобождаем лишнюю помпу целиком
                // и через общий путь закрытия, чтобы не осталось ни псевдоконсоли, ни учёта.
                ReleasePump(pump, "освобождение лишней помпы");
                return;
            }

            // Пока поднималась псевдоконсоль (а это CreateProcess), вкладку могли закрыть
            // или закрыться могло всё окно.
            //
            // Решение о выходе принимается по учёту вкладок, а НЕ по тому, досталась ли нам
            // помпа обратно. Закрытие снимает вкладку с учёта и забирает помпу из словаря
            // двумя отдельными действиями, и между ними мы могли успеть её добавить: тогда
            // помпу заберёт закрытие, а сюда вернётся пусто. Условие «и удалось забрать»
            // на этом порядке проваливалось бы дальше — к pump.Start() на помпе, которую
            // в этот момент освобождают, то есть к циклу на уже освобождённом токене.
            if (Volatile.Read(ref _disposed) == 1 || !Contains(args.TerminalId))
            {
                if (_pumps.TryRemove(args.TerminalId.Value, out var orphan))
                {
                    ReleasePump(orphan, "освобождение помпы закрытой вкладки");
                }

                return;
            }

            var terminalId = args.TerminalId;

            // Оболочка может завершиться сама (пользователь набрал exit, процесс упал).
            // Без этой подписки псевдоконсоль оставалась бы открытой до закрытия приложения:
            // ConPtySession достижим из _pumps, и финализатор SafeHandle не сработает.
            // Сама вкладка на странице при этом остаётся — с пометкой о коде выхода
            // (раздел 8 ТЗ), поэтому страницу закрывать терминал не просим.
            session.Exited += (_, exit) => OnShellExited(terminalId, exit.ExitCode);

            pump.Start();

            if (pending.StartupInput.Count > 0)
            {
                var startup = Task.Run(() => SendStartupInputAsync(pump, pending.StartupInput, _cts.Token));
                Track(startup);
                Observe(startup, "запись команды запуска в stdin");
            }
        }
        catch (PtyStartException exception)
        {
            // Оболочку поднять не удалось — пользователь должен увидеть причину прямо в терминале,
            // а не в молчаливо закрытой вкладке (раздел 8 ТЗ).
            ReportToTerminal(args.TerminalId, exception.Message);

            // И тем же событием, что и обычный выход: вкладка осталась на странице, но
            // псевдоконсоли под ней нет — без сигнала прикладной код считал бы её живой
            // и работающей навсегда. Наружу сбой подъёма не уходит: OpenAsync к этому
            // моменту давно вернулась, поднимать стало бы некому.
            RaiseExited(args.TerminalId, StartFailureExitCode);
        }
    }

    /// <summary>
    /// Пишет в stdin строки запуска сессии — раздел 5.1 ТЗ фиксирует именно запись в stdin,
    /// а не запуск оболочки с <c>-Command</c>.
    /// <para>
    /// Сигнала готовности readline у оболочки нет, а разбирать вывод в поисках приглашения
    /// запрещено (раздел 7 CLAUDE.md). Поэтому строки пишутся после первого байта вывода:
    /// до него оболочка заведомо не дошла до чтения stdin. Это признак по времени, а не по
    /// содержимому — что именно пришло, здесь не смотрит никто.
    /// </para>
    /// </summary>
    private async Task SendStartupInputAsync(
        TerminalPump pump,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            bool shellIsGone = false;

            try
            {
                shellIsGone = !await pump.FirstOutputReceived
                    .WaitAsync(_options.StartupOutputTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Оболочка молчит дольше отведённого. Команду пишем всё равно: потерянный
                // запуск хуже, чем запуск, который пользователь увидит в приглашении.
            }

            if (shellIsGone)
            {
                // Вкладку закрыли или поток вывода кончился раньше, чем оболочка отозвалась.
                return;
            }

            if (_options.StartupInputDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.StartupInputDelay, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (string line in lines)
            {
                // Строго по одной и по порядку: preLaunch обязан выполниться до claude
                // (раздел 5.1 ТЗ). Строки уже завершены переводом строки — это контракт
                // ISessionCommandBuilder.
                await pump.SendInputAsync(Encoding.UTF8.GetBytes(line), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or ObjectDisposedException
                                              or IOException)
        {
            // Вкладку закрыли между ready и записью. Потерять запуск допустимо,
            // уронить приложение из фоновой задачи — нет.
        }
    }

    /// <summary>
    /// Страница отдала ввод — он безусловно уходит в stdin псевдоконсоли и больше никуда.
    /// </summary>
    /// <remarks>
    /// Состояние вкладки отсюда не выводится: в этот же канал страница отдаёт ответы терминала
    /// на запросы программы — отчёт о фокусе, Device Attributes, цвет, размер, — и переключение
    /// вкладок выглядело бы вводом пользователя. Источник состояния — только хуки
    /// (раздел 7 CLAUDE.md).
    /// </remarks>
    private async void OnInputReceived(object? sender, TerminalInputEventArgs args)
    {
        // У мёртвой вкладки помпы нет — писать некуда.
        if (!_pumps.TryGetValue(args.TerminalId.Value, out var pump))
        {
            return;
        }

        try
        {
            await pump.SendInputAsync(args.Data, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or ObjectDisposedException
                                              or IOException)
        {
            // Вкладку закрыли, пока пользователь печатал. Потерять символ допустимо,
            // уронить процесс из async void — нет.
        }
    }

    /// <summary>
    /// Пишет сообщение прямо в терминал вкладки. Помпы у такой вкладки нет, поэтому её учёт
    /// записей в мосте снимется только при освобождении моста — это ограничено временем
    /// жизни приложения и числом неудачных запусков оболочки, а не растёт со временем.
    /// </summary>
    private void ReportToTerminal(TerminalId terminalId, string message)
    {
        const string Red = "[31m";
        const string Reset = "[0m\r\n";

        byte[] bytes = Encoding.UTF8.GetBytes(Red + message + Reset);
        Observe(
            _bridge.WriteOutputAsync(terminalId, bytes, CancellationToken.None).AsTask(),
            "вывод сообщения об ошибке во вкладку");
    }

    /// <summary>
    /// Доводит исход фоновой операции до места, где его видно. Полноценного журнала
    /// в приложении пока нет (появится вместе с приёмником хуков в M4), поэтому сбой
    /// уходит в <see cref="System.Diagnostics.Trace"/>, а не теряется молча.
    /// </summary>
    private static void Observe(Task task, string what) =>
        _ = task.ContinueWith(
            (completed, state) => System.Diagnostics.Trace.TraceError(
                "Claude Agents Shell: {0} завершилось ошибкой: {1}",
                state,
                completed.Exception),
            what,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void OnResizeRequested(object? sender, TerminalResizeEventArgs args)
    {
        if (_pumps.TryGetValue(args.TerminalId.Value, out var pump))
        {
            pump.Resize(args.Size);
        }
    }

    /// <summary>
    /// Вкладка, которую страница ещё не подтвердила: чем поднимать оболочку и что писать
    /// в её stdin, когда придёт <c>ready</c>.
    /// </summary>
    private readonly record struct PendingTerminal(PtyStartInfo StartInfo, IReadOnlyList<string> StartupInput);
}
