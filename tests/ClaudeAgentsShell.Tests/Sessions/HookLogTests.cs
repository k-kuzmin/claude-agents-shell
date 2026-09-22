using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Hooks;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class HookLogTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 22, 10, 15, 30, 250, TimeSpan.Zero);

    [Fact]
    public async Task Одна_строка_на_хук_со_всеми_полями_и_началом_токена()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");

        await using (var log = CreateLog(temp, appData))
        {
            log.Record(new HookEvent(
                HookKind.Stop,
                "9f2c0f4e",
                @"D:\src\domovoy",
                "0123456789abcdef",
                Moment,
                Source: "user",
                AgentId: "a-17",
                BackgroundTasks: [new BackgroundTask("t-1", "subagent"), new BackgroundTask("t-2", "shell")]));
        }

        var lines = await File.ReadAllLinesAsync(Path.Combine(appData, HookLog.FileName), CancellationToken.None);

        var line = Assert.Single(lines);
        Assert.Equal(
            "2026-09-22 10:15:30.250 +00:00 Stop source=user session=9f2c0f4e agent=a-17 bg=2[subagent,shell] token=01234567",
            line);

        // Токен целиком в журнал не попадает: по журналу нельзя подделать хук чужой вкладки.
        Assert.DoesNotContain("89abcdef", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Отсутствующие_поля_и_пустой_список_фоновых_задач_различимы()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");

        await using (var log = CreateLog(temp, appData))
        {
            log.Record(new HookEvent(HookKind.PostToolBatch, null, null, null, Moment));
            log.Record(new HookEvent(HookKind.Stop, null, null, "tab", Moment, BackgroundTasks: []));
        }

        var lines = await File.ReadAllLinesAsync(Path.Combine(appData, HookLog.FileName), CancellationToken.None);

        Assert.Equal(
            [
                "2026-09-22 10:15:30.250 +00:00 PostToolBatch source=- session=- agent=- bg=- token=-",
                "2026-09-22 10:15:30.250 +00:00 Stop source=- session=- agent=- bg=0 token=tab",
            ],
            lines);
    }

    [Fact]
    public async Task Перевод_строки_в_поле_не_рвёт_журнал()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");

        await using (var log = CreateLog(temp, appData))
        {
            log.Record(new HookEvent(HookKind.SessionStart, "a\r\nfake line", null, null, Moment, Source: "st art"));
        }

        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(appData, HookLog.FileName), CancellationToken.None));
        Assert.Contains("session=a__fake_line", line, StringComparison.Ordinal);
        Assert.Contains("source=st_art", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Переполненный_файл_уезжает_в_предыдущий_и_запись_начинается_заново()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        Directory.CreateDirectory(appData);

        var path = Path.Combine(appData, HookLog.FileName);
        await File.WriteAllBytesAsync(path, new byte[HookLog.MaxBytes], CancellationToken.None);

        await using (var log = CreateLog(temp, appData))
        {
            log.Record(new HookEvent(HookKind.Stop, "s", null, null, Moment));
        }

        Assert.Equal(HookLog.MaxBytes, new FileInfo(Path.Combine(appData, HookLog.PreviousFileName)).Length);
        Assert.Single(await File.ReadAllLinesAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Занятый_чужим_процессом_файл_не_роняет_журнал_и_следующие_записи_доходят()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        Directory.CreateDirectory(appData);
        var path = Path.Combine(appData, HookLog.FileName);

        await using var log = CreateLog(temp, appData);

        using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Record(new HookEvent(HookKind.Stop, "потерянный", null, null, Moment));

            // Запись идёт в фоне: дать ей упереться в занятый файл, пока он занят.
            await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        }

        log.Record(new HookEvent(HookKind.Stop, "дошедший", null, null, Moment));
        await log.DisposeAsync();

        var text = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains("session=дошедший", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Недоступный_каталог_данных_не_бросает_ни_при_записи_ни_при_закрытии()
    {
        using var temp = new TempDirectory();

        // Каталог данных на месте файла: создать его нельзя, любой ввод-вывод падает.
        var appData = temp.Combine("appdata");
        await File.WriteAllTextAsync(appData, "не каталог", CancellationToken.None);

        var log = CreateLog(temp, appData);
        log.Record(new HookEvent(HookKind.Stop, "s", null, null, Moment));
        log.Record(new HookEvent(HookKind.SessionEnd, "s", null, null, Moment));

        await log.DisposeAsync();

        // Запись после закрытия тоже молча теряется, а не бросает.
        log.Record(new HookEvent(HookKind.Stop, "s", null, null, Moment));
    }

    [Fact]
    public async Task Неожиданное_исключение_путей_теряет_пачку_но_не_цикл_записи()
    {
        using var temp = new TempDirectory();
        var paths = new ThrowingOncePaths(temp.Combine("appdata"));

        var log = new HookLog(paths, new UtcTimeProvider());
        log.Record(new HookEvent(HookKind.Stop, "потерянный", null, null, Moment));

        // Первая пачка упёрлась в исключение вне списка ввода-вывода.
        await paths.Thrown.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);

        log.Record(new HookEvent(HookKind.Stop, "дошедший", null, null, Moment));
        await log.DisposeAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(
            Path.Combine(temp.Combine("appdata"), HookLog.FileName),
            CancellationToken.None));
        Assert.Contains("session=дошедший", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Закрытие_не_пробрасывает_сбой_записи()
    {
        var paths = new AlwaysThrowingPaths();

        var log = new HookLog(paths, new UtcTimeProvider());
        log.Record(new HookEvent(HookKind.Stop, "s", null, null, Moment));
        log.Record(new HookEvent(HookKind.SessionEnd, "s", null, null, Moment));

        var failure = await Record.ExceptionAsync(() => log.DisposeAsync().AsTask());

        Assert.Null(failure);
        Assert.True(paths.Calls > 0);
    }

    private static HookLog CreateLog(TempDirectory temp, string appData) =>
        new(new AppDataPaths(appData, temp.Combine("claude", "projects")), new UtcTimeProvider());

    /// <summary>Каталог данных, который в первый раз бросает не-IO исключение, а дальше работает.</summary>
    private sealed class ThrowingOncePaths(string appData) : IAppDataPaths
    {
        private readonly TaskCompletionSource _thrown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Thrown => _thrown.Task;

        public string AppData
        {
            get
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    _thrown.TrySetResult();
                    throw new InvalidOperationException("пути ещё не готовы");
                }

                Directory.CreateDirectory(appData);
                return appData;
            }
        }

        public string ProjectsFile => Path.Combine(appData, "projects.json");

        public string ClaudeProjects => Path.Combine(appData, "claude");
    }

    /// <summary>Каталог данных, который бросает всегда.</summary>
    private sealed class AlwaysThrowingPaths : IAppDataPaths
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string AppData
        {
            get
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("путей нет");
            }
        }

        public string ProjectsFile => throw new InvalidOperationException("путей нет");

        public string ClaudeProjects => throw new InvalidOperationException("путей нет");
    }

    private sealed class UtcTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
