using System.Diagnostics;
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

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
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

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);
        session.RaiseExited(0);
        await WaitUntilAsync(() => session.IsDisposed);

        Assert.Contains(terminalId.Value, bridge.CreatedTerminals);
        Assert.DoesNotContain(terminalId.Value, bridge.ClosedTerminals);
    }

    [Fact]
    public async Task Ввод_в_завершённую_вкладку_никуда_не_идёт()
    {
        var factory = new FakePtySessionFactory();
        var bridge = new FakeTerminalBridge();
        await using var workspace = CreateWorkspace(bridge, factory);

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
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

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);

        Assert.True(session.IsDisposed);
        Assert.Contains(terminalId.Value, bridge.ClosedTerminals);

        bridge.RaiseInput(terminalId, new byte[] { 7 });
        await Task.Delay(50);
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
            var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
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

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
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

        var terminalId = await workspace.OpenAsync(ShellKind.Pwsh, TestDirectory, CancellationToken.None);
        bridge.RaiseReady(terminalId);

        var session = await WaitForSessionAsync(factory);

        await workspace.CloseAsync(terminalId, CancellationToken.None);
        await workspace.CloseAsync(terminalId, CancellationToken.None);
        session.RaiseExited(0);

        await Task.Delay(50);

        Assert.Equal(1, session.DisposeCount);
    }

    private static string TestDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static TerminalWorkspace CreateWorkspace(FakeTerminalBridge bridge, FakePtySessionFactory factory) =>
        new(bridge, factory, new FakeShellResolver(), new TerminalOptions(), TimeProvider.System);

    private static async Task<FakePtySession> WaitForSessionAsync(FakePtySessionFactory factory)
    {
        await WaitUntilAsync(() => factory.Created.Count > 0);
        return factory.Created.First();
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
