using System.Diagnostics;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.Output;

public sealed class TerminalWorkspaceTests
{
    /// <summary>
    /// Главное свойство: оболочка вышла сама — псевдоконсоль обязана освободиться.
    /// Иначе процесс хоста консоли живёт до закрытия приложения, а <c>SafeHandle</c>
    /// не спасает: сессия достижима из карты вкладок, финализатор не сработает.
    /// </summary>
    [Fact]
    public async Task Самостоятельный_выход_оболочки_освобождает_псевдоконсоль()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        // Пользователь набрал exit: процесс завершился сам, без закрытия вкладки.
        session.RaiseExited(0);

        await WaitUntilAsync(() => session.IsDisposed);

        Assert.True(session.IsDisposed);
    }

    /// <summary>
    /// Вкладка на странице при этом остаётся: пользователь должен увидеть код выхода
    /// (раздел 8 ТЗ), а не потерять терминал вместе с историей.
    /// </summary>
    [Fact]
    public async Task Выход_оболочки_не_уничтожает_терминал_на_странице()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);
        session.RaiseExited(0);
        await WaitUntilAsync(() => session.IsDisposed);

        Assert.Contains(terminalId.Value, bridge.CreatedTerminals);
        Assert.DoesNotContain(terminalId.Value, bridge.ClosedTerminals);
        Assert.Contains(terminalId, workspace.Terminals);
    }

    /// <summary>
    /// Код выхода должен дойти до того, кто рисует вкладки: закрывать её самостоятельно
    /// нельзя, значит пометку ставит прикладной код по этому событию.
    /// </summary>
    [Fact]
    public async Task Выход_оболочки_поднимает_событие_с_кодом_выхода()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        TerminalExitedEventArgs? observed = null;
        workspace.TerminalExited += (_, args) => observed = args;

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);
        session.RaiseExited(3);

        await WaitUntilAsync(() => observed is not null);

        Assert.Equal(terminalId, observed!.TerminalId);
        Assert.Equal(3, observed.ExitCode);
    }

    [Fact]
    public async Task Ввод_в_завершённую_вкладку_никуда_не_идёт()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);
        session.RaiseExited(0);
        await WaitUntilAsync(() => session.IsDisposed);

        bridge.RaiseInput(terminalId, new byte[] { 1, 2, 3 });
        await Task.Delay(50);

        Assert.Empty(session.WrittenInput);
    }

    [Fact]
    public async Task Закрытие_вкладки_убирает_её_из_маршрутизации_и_со_страницы()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);

        Assert.True(session.IsDisposed);
        Assert.Contains(terminalId.Value, bridge.ClosedTerminals);
        Assert.Empty(workspace.Terminals);

        bridge.RaiseInput(terminalId, new byte[] { 7 });
        await Task.Delay(50);
        Assert.Empty(session.WrittenInput);
    }

    /// <summary>
    /// Пять вкладок в одном WebView2 различаются только идентификатором, поэтому
    /// перепутанная маршрутизация вывода не упала бы, а тихо смешала бы сессии.
    /// </summary>
    [Fact]
    public async Task Вывод_одной_вкладки_не_попадает_в_другую()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var (first, second) = await OpenPairAsync(workspace, bridge, factory);

        factory.At(0).Emit(1, 2, 3);
        factory.At(1).Emit(9, 9);

        await WaitUntilAsync(() => bridge.BytesFor(first).Length == 3 && bridge.BytesFor(second).Length == 2);

        Assert.Equal(new byte[] { 1, 2, 3 }, bridge.BytesFor(first));
        Assert.Equal(new byte[] { 9, 9 }, bridge.BytesFor(second));
    }

    /// <summary>
    /// Ввод пользователя уходит ровно в ту псевдоконсоль, из которой пришёл.
    /// </summary>
    [Fact]
    public async Task Ввод_идёт_в_свою_псевдоконсоль()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var (first, second) = await OpenPairAsync(workspace, bridge, factory);

        bridge.RaiseInput(first, new byte[] { 65 });
        bridge.RaiseInput(second, new byte[] { 66 });

        await WaitUntilAsync(() => factory.At(0).WrittenInput.Count == 1 && factory.At(1).WrittenInput.Count == 1);

        Assert.Equal([65], factory.At(0).WrittenInput);
        Assert.Equal([66], factory.At(1).WrittenInput);
    }

    /// <summary>
    /// Страница считает размер один раз и шлёт <c>resize</c> на каждый идентификатор,
    /// в том числе скрытым вкладкам. Со стороны C# это значит: размер доходит до своей
    /// псевдоконсоли и только до неё, независимо от того, видима вкладка или нет.
    /// </summary>
    [Fact]
    public async Task Размер_доходит_до_каждой_вкладки_и_не_смешивается()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var (first, second) = await OpenPairAsync(workspace, bridge, factory);

        // Вторая вкладка видима, первая скрыта — страница присылает размер обеим.
        bridge.RaiseResize(first, new TerminalSize(100, 30));
        bridge.RaiseResize(second, new TerminalSize(100, 30));

        await WaitUntilAsync(() => factory.At(0).Resizes.Count == 1 && factory.At(1).Resizes.Count == 1);

        Assert.Equal(new TerminalSize(100, 30), factory.At(0).Resizes[0]);
        Assert.Equal(new TerminalSize(100, 30), factory.At(1).Resizes[0]);
    }

    /// <summary>
    /// Переключение вкладки — только смена видимости: ни новой псевдоконсоли, ни ресайза,
    /// ни освобождения старой (раздел 7 ТЗ, критерий 2).
    /// </summary>
    [Fact]
    public async Task Активация_не_создаёт_псевдоконсоль_и_не_меняет_размер()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var (first, second) = await OpenPairAsync(workspace, bridge, factory);

        bridge.RaiseResize(first, new TerminalSize(100, 30));
        await WaitUntilAsync(() => factory.At(0).Resizes.Count == 1);

        await workspace.ActivateAsync(first, CancellationToken.None);

        // Даём время на работу, которой быть не должно.
        await Task.Delay(50);

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal([new TerminalSize(100, 30)], factory.At(0).Resizes);
        Assert.Empty(factory.At(1).Resizes);
        Assert.All(factory.Created, static session => Assert.False(session.IsDisposed));
        Assert.Equal(first.Value, bridge.ShownTerminals[^1]);
        Assert.Equal([first, second], workspace.Terminals);
    }

    /// <summary>
    /// Неизвестный идентификатор не должен уходить на страницу: <c>show</c> скрывает всё,
    /// кроме указанного, и вместо переключения вкладки получилось бы пустое окно.
    /// </summary>
    [Fact]
    public async Task Активация_несуществующей_вкладки_не_гасит_страницу()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        await workspace.ActivateAsync(new TerminalId("несуществующая"), CancellationToken.None);

        Assert.Equal([terminalId.Value], bridge.ShownTerminals);
    }

    /// <summary>
    /// Закрытие одной вкладки не трогает соседние: их псевдоконсоли живы и продолжают
    /// принимать ввод.
    /// </summary>
    [Fact]
    public async Task Закрытие_одной_вкладки_не_трогает_остальные()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var (first, second) = await OpenPairAsync(workspace, bridge, factory);

        await workspace.CloseAsync(first, CancellationToken.None);

        Assert.True(factory.At(0).IsDisposed);
        Assert.False(factory.At(1).IsDisposed);
        Assert.Equal([second], workspace.Terminals);
        Assert.Equal([first.Value], bridge.ClosedTerminals);

        bridge.RaiseInput(second, new byte[] { 42 });
        await WaitUntilAsync(() => factory.At(1).WrittenInput.Count == 1);

        Assert.Equal([42], factory.At(1).WrittenInput);
    }

    /// <summary>
    /// Раздел 5.1 ТЗ фиксирует запуск записью в stdin. Признака готовности readline у нас
    /// нет, а разбирать вывод запрещено, поэтому строки пишутся после первого байта вывода —
    /// и строго по порядку, иначе <c>preLaunch</c> выполнится после <c>claude</c>.
    /// </summary>
    [Fact]
    public async Task Команда_запуска_пишется_в_stdin_после_первого_байта_вывода()
    {
        string[] lines = ["cd repo\r", "claude --continue\r"];

        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory, lines);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        // Оболочка ещё не отозвалась — писать в её stdin рано.
        await Task.Delay(100);
        Assert.Empty(session.WrittenInput);

        session.Emit(27);

        byte[] expected = Encoding.UTF8.GetBytes(string.Concat(lines));
        await WaitUntilAsync(() => session.WrittenInput.Count == expected.Length);

        Assert.Equal(expected, session.WrittenInput);
    }

    /// <summary>
    /// Вкладку закрыли раньше, чем оболочка отозвалась: команда запуска не должна уйти
    /// в освобождённую псевдоконсоль, а фоновая задача — пережить закрытие.
    /// </summary>
    [Fact]
    public async Task Закрытие_до_первого_байта_отменяет_запись_команды()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory, "claude\r");

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);
        await Task.Delay(100);

        Assert.Empty(session.WrittenInput);
    }

    /// <summary>
    /// Вкладки закрываются параллельно: у каждой свой бюджет ожидания выхода процесса,
    /// и последовательное закрытие умножало бы задержку на число вкладок, держа окно
    /// на экране всё это время.
    /// </summary>
    [Fact]
    public async Task Вкладки_закрываются_параллельно_а_не_по_очереди()
    {
        var slowDispose = TimeSpan.FromMilliseconds(300);
        const int tabs = 5;

        var factory = new FakePtySessionFactory(slowDispose);
        var bridge = new FakeTerminalBridge();
        var workspace = CreateWorkspace(bridge, factory);

        for (int i = 0; i < tabs; i++)
        {
            var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
            bridge.RaiseReady(terminalId);
        }

        await WaitUntilAsync(() => factory.Created.Count == tabs);

        var stopwatch = Stopwatch.StartNew();
        await workspace.DisposeAsync();
        stopwatch.Stop();

        Assert.All(factory.Created, static session => Assert.True(session.IsDisposed));

        // Последовательное закрытие заняло бы не меньше tabs * 300 мс.
        Assert.True(
            stopwatch.Elapsed < slowDispose * (tabs - 1),
            $"Закрытие {tabs} вкладок заняло {stopwatch.ElapsedMilliseconds} мс — похоже на последовательное.");
    }

    /// <summary>
    /// Оболочка вышла ровно в момент закрытия окна: помпа уже убрана из маршрутизации,
    /// и без учёта начатых гашений <c>DisposeAsync</c> прошёл бы мимо — приложение
    /// завершилось бы, не дождавшись закрытия псевдоконсоли.
    /// </summary>
    [Fact]
    public async Task Освобождение_дожидается_гашения_начатого_выходом_оболочки()
    {
        var slowDispose = TimeSpan.FromMilliseconds(400);
        var factory = new FakePtySessionFactory(slowDispose);
        var bridge = new FakeTerminalBridge();
        var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        // Выход оболочки запускает гашение в фоне; сразу же закрываем приложение.
        session.RaiseExited(0);
        await workspace.DisposeAsync();

        Assert.True(
            session.IsDisposed,
            "DisposeAsync вернулся, пока псевдоконсоль ещё гасилась.");
    }

    [Fact]
    public async Task Повторное_закрытие_безвредно()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);
        await workspace.CloseAsync(terminalId, CancellationToken.None);
        session.RaiseExited(0);

        await Task.Delay(50);

        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>
    /// Закрытие вкладки гасит оболочку — её выход это следствие, а не событие для интерфейса.
    /// Иначе прикладной код получал бы «процесс завершился» на вкладку, которую сам только что
    /// убрал, и помечал бы несуществующую строку.
    /// </summary>
    [Fact]
    public async Task Закрытая_вкладка_не_поднимает_событие_выхода()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var exits = new List<TerminalId>();
        workspace.TerminalExited += (_, args) => exits.Add(args.TerminalId);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(terminalId);
        await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);
        await Task.Delay(50);

        Assert.Empty(exits);
    }

    /// <summary>
    /// Вкладку закрыли, пока страница поднимала терминал: <c>ready</c> уже забрал заявку,
    /// а закрытие ещё не нашло помпы. Без сверки с учётом после добавления осталась бы
    /// открытая псевдоконсоль без вкладки — до конца жизни приложения.
    /// </summary>
    [Fact]
    public async Task Закрытие_во_время_подъёма_псевдоконсоли_не_оставляет_её_открытой()
    {
        var inner = new FakePtySessionFactory();
        var gated = new GatedPtySessionFactory(inner);
        var bridge = new FakeTerminalBridge();

        await using var workspace = new TerminalWorkspace(
            bridge,
            gated,
            new FakeShellResolver(),
            new FakeSessionCommandBuilder(),
            TestOptions,
            TimeProvider.System);

        var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);

        var ready = Task.Run(() => bridge.RaiseReady(terminalId));
        await gated.WaitForEntryAsync();

        // Помпы ещё нет: закрытие её не найдёт.
        await workspace.CloseAsync(terminalId, CancellationToken.None);

        gated.Release();
        await ready;

        await WaitUntilAsync(() => inner.Created.Count == 1 && inner.At(0).IsDisposed);

        Assert.True(inner.At(0).IsDisposed);
        Assert.Empty(workspace.Terminals);
    }

    /// <summary>
    /// Сценарий раздела 3.5 ТЗ: двадцать вкладок открыть и закрыть подряд. Со стороны C#
    /// проверяемое свойство — ни одной пережившей закрытие псевдоконсоли и ни одного
    /// идентификатора, оставшегося в учёте.
    /// </summary>
    [Fact]
    public async Task Двадцать_циклов_открытия_и_закрытия_не_оставляют_следов()
    {
        const int cycles = 20;

        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        for (int i = 0; i < cycles; i++)
        {
            var terminalId = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
            bridge.RaiseReady(terminalId);
            await WaitUntilAsync(() => factory.Created.Count == i + 1);

            await workspace.CloseAsync(terminalId, CancellationToken.None);
        }

        Assert.Empty(workspace.Terminals);
        Assert.Equal(cycles, factory.Created.Count);
        Assert.All(factory.Created, static session => Assert.True(session.IsDisposed));
        Assert.Equal(cycles, bridge.ClosedTerminals.Count);
    }

    /// <summary>
    /// Оболочку найти не удалось — вкладки не появляется вовсе: идентификатор без
    /// псевдоконсоли остался бы в списке навсегда.
    /// </summary>
    [Fact]
    public async Task Ненайденная_оболочка_не_оставляет_вкладку_в_списке()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = new TerminalWorkspace(
            bridge,
            factory,
            new FailingShellResolver(),
            new FakeSessionCommandBuilder(),
            TestOptions,
            TimeProvider.System);

        await Assert.ThrowsAsync<ShellNotFoundException>(
            () => workspace.OpenAsync(Project(), Launch, CancellationToken.None));

        Assert.Empty(workspace.Terminals);
        Assert.Empty(bridge.CreatedTerminals);
    }

    private static SessionLaunch Launch => new SessionLaunch.NewSession();

    private static string TestDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Пауза перед записью в stdin в тестах не нужна: проверяется порядок, а не запас по времени.</summary>
    private static TerminalOptions TestOptions => new() { StartupInputDelay = TimeSpan.Zero };

    private static ProjectDefinition Project() =>
        new(Guid.NewGuid(), "Проект", TestDirectory, ShellKind.Pwsh, null, [], 0);

    private static TerminalWorkspace CreateWorkspace(
        FakeTerminalBridge bridge,
        FakePtySessionFactory factory,
        params string[] startupInput) =>
        new(
            bridge,
            factory,
            new FakeShellResolver(),
            new FakeSessionCommandBuilder(startupInput),
            TestOptions,
            TimeProvider.System);

    /// <summary>Открывает две вкладки и дожидается, пока обе получат псевдоконсоль.</summary>
    private static async Task<(TerminalId First, TerminalId Second)> OpenPairAsync(
        TerminalWorkspace workspace,
        FakeTerminalBridge bridge,
        FakePtySessionFactory factory)
    {
        var first = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(first);
        await WaitUntilAsync(() => factory.Created.Count == 1);

        var second = await workspace.OpenAsync(Project(), Launch, CancellationToken.None);
        bridge.RaiseReady(second);
        await WaitUntilAsync(() => factory.Created.Count == 2);

        return (first, second);
    }

    private static async Task<FakePtySession> WaitForSessionAsync(FakePtySessionFactory factory)
    {
        await WaitUntilAsync(() => factory.Created.Count > 0);
        return factory.At(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Условие не выполнилось за отведённое время.");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>
/// Сборщик команды запуска — заглушка: отдаёт заранее заданные строки. Настоящая реализация
/// живёт в слое Sessions; здесь проверяется только то, когда и в каком порядке эти строки
/// попадают в stdin.
/// </summary>
internal sealed class FakeSessionCommandBuilder(params string[] lines) : ISessionCommandBuilder
{
    public IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch) => lines;
}

/// <summary>
/// Фабрика, которая замирает внутри <see cref="Create"/>, пока её не отпустят. Так
/// воспроизводится гонка «страница прислала ready, пользователь закрыл вкладку»: подъём
/// псевдоконсоли — это CreateProcess, и он занимает заметное время.
/// </summary>
internal sealed class GatedPtySessionFactory(FakePtySessionFactory inner) : IPtySessionFactory
{
    private readonly SemaphoreSlim _entered = new(0);
    private readonly SemaphoreSlim _release = new(0);

    public IPtySession Create(PtyStartInfo startInfo)
    {
        _entered.Release();
        _release.Wait();
        return inner.Create(startInfo);
    }

    /// <summary>Ждёт, пока подъём псевдоконсоли начался.</summary>
    public Task WaitForEntryAsync() => _entered.WaitAsync();

    /// <summary>Отпускает подъём псевдоконсоли.</summary>
    public void Release() => _release.Release();
}

/// <summary>Резолвер, который ничего не находит: так выглядит система без единой оболочки.</summary>
internal sealed class FailingShellResolver : IShellResolver
{
    public IReadOnlyList<ShellKind> Available => [];

    public ShellStartCommand Resolve(ShellKind preferred) =>
        throw new ShellNotFoundException("Оболочки не найдены.");
}
