using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Координатор состояний вкладок (раздел 5.3 ТЗ): хуки и ввод пользователя превращаются
/// в состояние вкладки и короткое имя сессии, а всё непонятное молча пропускается.
/// </summary>
public sealed class SessionStateCoordinatorTests
{
    private const string ProjectPath = @"D:\src\alpha";
    private const string HookPath = @"D:\src\alpha-from-hook";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task SessionStart_переводит_вкладку_в_простаивает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Stop_переводит_вкладку_в_ждёт_ввода()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task SessionEnd_снимает_маркер()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.RaiseHook(HookKind.SessionEnd, tab);

        Assert.Equal(TabState.Unknown, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Ввод_пользователя_переводит_вкладку_в_работает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        harness.Workspace.RaiseUserInput(tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Ввод_в_свежую_сессию_тоже_переводит_в_работает()
    {
        // Отступление от буквы раздела 5.3 ТЗ («первый ввод после Stop») согласовано:
        // у сессии, которая в «ждёт ввода» ещё не была, точка иначе не менялась бы вовсе.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.Workspace.RaiseUserInput(tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Полный_цикл_состояний_проходит_по_порядку()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.Workspace.RaiseUserInput(tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.Workspace.RaiseUserInput(tab);
        harness.RaiseHook(HookKind.SessionEnd, tab);

        Assert.Equal(
            new[] { TabState.Idle, TabState.Busy, TabState.AwaitingInput, TabState.Busy, TabState.Unknown },
            harness.Sink.StateLog.Select(entry => entry.State));
    }

    [Fact]
    public async Task Неизвестный_токен_ничего_не_меняет()
    {
        using var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Hooks.Raise(HookKind.SessionStart, "tok-чужой", SessionId, ProjectPath);
        harness.Hooks.Raise(HookKind.Stop, null, SessionId, ProjectPath);

        Assert.Empty(harness.Sink.StateLog);
        Assert.Empty(harness.History.Requested);
    }

    [Fact]
    public async Task Незарегистрированный_хук_состояние_не_трогает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Unknown, tab);

        Assert.Empty(harness.Sink.StateLog);
    }

    [Fact]
    public async Task Хук_доходит_до_вкладки_только_через_диспетчер()
    {
        // HttpListener поднимает событие в потоке пула: трогать полосу вкладок оттуда нельзя.
        var dispatcher = new QueuedUiDispatcher();
        using var harness = new Harness(dispatcher);
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);

        Assert.Empty(harness.Sink.StateLog);
        Assert.Equal(1, dispatcher.PostCount);

        dispatcher.Drain();

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Ввод_пользователя_через_диспетчер_не_гоняется()
    {
        // Горячий путь ввода: событие уже поднято в потоке интерфейса, и лишнее замыкание
        // с очередью диспетчера на каждое нажатие клавиши здесь недопустимо.
        var dispatcher = new QueuedUiDispatcher();
        using var harness = new Harness(dispatcher);
        var tab = await harness.StartWithTabAsync();

        harness.Workspace.RaiseUserInput(tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
        Assert.Equal(0, dispatcher.PostCount);
    }

    [Fact]
    public async Task SessionStart_ставит_короткое_имя_из_первого_сообщения()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку и добавь тесты");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку и добавь тесты", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Транскрипт_ищется_в_каталоге_из_хука()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "привет");

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: HookPath);
        await harness.SettleAsync();

        Assert.Equal((HookPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Без_каталога_в_хуке_берётся_каталог_вкладки()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "привет");

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: null);
        await harness.SettleAsync();

        Assert.Equal((ProjectPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Без_каталога_и_без_вкладки_транскрипт_не_читается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync(knownToSink: false);

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: null);
        await harness.SettleAsync();

        Assert.Empty(harness.History.Requested);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Без_идентификатора_сессии_транскрипт_не_читается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: null);
        await harness.SettleAsync();

        Assert.Empty(harness.History.Requested);
        Assert.Empty(harness.Sink.ShortTitles);
    }

    [Fact]
    public async Task Отсутствие_транскрипта_не_роняет_и_имя_не_портит()
    {
        // Файл транскрипта создаётся не в момент SessionStart — это не ошибка,
        // заголовок вкладки остаётся «новая сессия».
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ShortTitles);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Имя_дочитывается_по_Stop_если_транскрипта_ещё_не_было()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();
        Assert.Empty(harness.Sink.ShortTitles);

        // К моменту Stop агент уже ответил, значит первое сообщение в транскрипте есть.
        harness.History.Seed(SessionId, "почини сборку");
        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
        Assert.Equal(2, harness.History.Requested.Count);
    }

    [Fact]
    public async Task Stop_без_SessionStart_тоже_даёт_имя()
    {
        // Приёмник мог подняться уже после старта сессии: тогда первым и единственным
        // известным хуком вкладки окажется Stop, и заголовок берётся из него.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");

        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
        Assert.Equal((ProjectPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Готовое_имя_второй_раз_не_вычитывается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Single(harness.History.Requested);
    }

    [Fact]
    public async Task Новая_сессия_во_вкладке_перечитывает_имя()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(other, "вторая сессия");
        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: other);
        await harness.SettleAsync();

        Assert.Equal("вторая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Сбой_чтения_транскрипта_не_роняет_приложение()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");
        harness.History.ReadFailure = new IOException("файл занят");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ShortTitles);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Заголовки_нескольких_вкладок_доезжают_независимо()
    {
        using var harness = new Harness();
        var first = await harness.StartWithTabAsync();
        var second = await harness.OpenTabAsync();

        const string secondSession = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(SessionId, "первая вкладка");
        harness.History.Seed(secondSession, "вторая вкладка");

        harness.RaiseHook(HookKind.SessionStart, first);
        harness.RaiseHook(HookKind.SessionStart, second, sessionId: secondSession);
        await harness.SettleAsync();

        Assert.Equal("первая вкладка", harness.Sink.ShortTitles[first]);
        Assert.Equal("вторая вкладка", harness.Sink.ShortTitles[second]);
    }

    [Fact]
    public async Task StartAsync_поднимает_приёмник_хуков()
    {
        using var harness = new Harness();
        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        Assert.True(harness.Hooks.Started);
    }

    [Fact]
    public async Task Сбой_подъёма_приёмника_не_роняет_запуск()
    {
        // Раздел 5.3 ТЗ: без хуков вкладки живут без маркеров, и это не повод показывать ошибку.
        using var harness = new Harness();
        harness.Hooks.StartFailure = new InvalidOperationException("порт занят");

        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        Assert.False(harness.Hooks.Started);

        var tab = await harness.OpenTabAsync();
        harness.Workspace.RaiseUserInput(tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Приёмник_хуков_координатором_не_освобождается()
    {
        // Владеет им контейнер: двойное освобождение — источник тихих гонок при выходе.
        var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();

        Assert.False(harness.Hooks.Disposed);
    }

    [Fact]
    public async Task После_Dispose_события_вкладок_не_трогают()
    {
        var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();
        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.Workspace.RaiseUserInput(tab);

        Assert.Empty(harness.Sink.StateLog);
    }

    [Fact]
    public async Task Повторный_Dispose_безопасен()
    {
        var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();
        harness.Coordinator.Dispose();
    }

    [Fact]
    public async Task Dispose_после_сбоя_запуска_безопасен()
    {
        var harness = new Harness();
        harness.Hooks.StartFailure = new InvalidOperationException("порт занят");
        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        harness.Coordinator.Dispose();
    }

    [Fact]
    public async Task Запуск_после_Dispose_отклоняется()
    {
        var harness = new Harness();
        harness.Coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None));
    }

    private sealed class Harness : IDisposable
    {
        private readonly QueuedUiDispatcher? _queued;
        private int _counter;

        public Harness(QueuedUiDispatcher? dispatcher = null)
        {
            _queued = dispatcher;
            Coordinator = new SessionStateCoordinator(
                Hooks, Workspace, History, (IUiDispatcher?)dispatcher ?? new InlineUiDispatcher());
        }

        public FakeHookListener Hooks { get; } = new();

        public FakeTerminalWorkspace Workspace { get; } = new();

        public FakeSessionHistoryReader History { get; } = new();

        public FakeTabStateSink Sink { get; } = new();

        public SessionStateCoordinator Coordinator { get; }

        /// <summary>Поднимает координатор и открывает вкладку, о которой знает полоса вкладок.</summary>
        public async Task<TerminalId> StartWithTabAsync(bool knownToSink = true)
        {
            await Coordinator.StartAsync(Sink, CancellationToken.None);
            return await OpenTabAsync(knownToSink);
        }

        /// <summary>Открывает ещё одну вкладку в наборе.</summary>
        public async Task<TerminalId> OpenTabAsync(bool knownToSink = true)
        {
            var project = new ProjectDefinition(
                Guid.NewGuid(), "alpha", ProjectPath, ShellKind.Pwsh, PreLaunch: null, ExtraArgs: [], Order: _counter++);

            var tab = await Workspace.OpenAsync(project, new SessionLaunch.NewSession(), CancellationToken.None);
            if (knownToSink)
            {
                Sink.SetWorkingDirectory(tab, ProjectPath);
            }

            return tab;
        }

        /// <summary>Отправляет хук от имени конкретной вкладки.</summary>
        public void RaiseHook(
            HookKind kind,
            TerminalId tab,
            string? sessionId = SessionId,
            string? workingDirectory = ProjectPath) =>
            Hooks.Raise(kind, Workspace.TokenFor(tab), sessionId, workingDirectory);

        /// <summary>
        /// Ждёт, пока догонит фоновое чтение транскрипта, и прокручивает очередь диспетчера.
        /// </summary>
        public async Task SettleAsync()
        {
            _queued?.Drain();
            await Coordinator.PendingTitleWork;
            _queued?.Drain();
        }

        public void Dispose() => Coordinator.Dispose();
    }
}
