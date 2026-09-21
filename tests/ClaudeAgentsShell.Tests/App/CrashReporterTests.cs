using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Решение «записать и показать» у глобального обработчика. Проверяется предохранитель от
/// лавины: сбой в отрисовке повторяется на каждом кадре, и окно на каждое исключение
/// превратило бы приложение в очередь диалогов, которую нечем закрыть.
/// </summary>
public sealed class CrashReporterTests
{
    private static readonly Exception Failure = new InvalidOperationException("сломалось");

    [Fact]
    public void Сбой_записывается_в_журнал_и_показывается_пользователю()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        var shown = reporter.Report("TestSource", Failure);

        Assert.True(shown);
        Assert.Equal(("TestSource", Failure), Assert.Single(log.Entries));
        Assert.Contains("сломалось", Assert.Single(prompt.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void В_окне_виден_путь_к_журналу()
    {
        var log = new FakeCrashLog { Path = @"C:\data\crash.log" };
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        reporter.Report("TestSource", Failure);

        Assert.Contains(@"C:\data\crash.log", Assert.Single(prompt.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Запись_без_окна_не_трогает_пользователя()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        reporter.Record("AppDomain", Failure);

        Assert.Single(log.Entries);
        Assert.Empty(prompt.Errors);
    }

    [Fact]
    public void Пока_окно_открыто_второго_не_показывается()
    {
        var log = new FakeCrashLog();
        CrashReporter? reporter = null;
        var nested = 0;

        // Сбой во время открытого окна: модальный показ крутит цикл сообщений, и ошибка
        // отрисовки приходит в тот же поток прямо из ShowError.
        var prompt = new ReentrantUserPrompt(() =>
        {
            if (nested++ == 0)
            {
                Assert.False(reporter!.Report("TestSource", Failure));
            }
        });

        reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        Assert.True(reporter.Report("TestSource", Failure));

        // Окон показано одно, а записей в журнале две: лавину глушит только диалог.
        Assert.Equal(1, prompt.Shown);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void Больше_порога_окон_за_одно_окно_времени_не_показывается()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        for (var i = 0; i < CrashReporter.MaxDialogsPerWindow; i++)
        {
            Assert.True(reporter.Report("TestSource", Failure));
        }

        Assert.False(reporter.Report("TestSource", Failure));
        Assert.Equal(CrashReporter.MaxDialogsPerWindow, prompt.Errors.Count);
        Assert.Equal(CrashReporter.MaxDialogsPerWindow + 1, log.Entries.Count);
    }

    [Fact]
    public void Последнее_окно_предупреждает_о_тишине()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        for (var i = 0; i < CrashReporter.MaxDialogsPerWindow; i++)
        {
            reporter.Report("TestSource", Failure);
        }

        Assert.DoesNotContain("только в журнал", prompt.Errors[0], StringComparison.Ordinal);
        Assert.Contains("только в журнал", prompt.Errors[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void По_истечении_окна_времени_показ_возобновляется()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var time = new ManualTimeProvider();
        var reporter = new CrashReporter(log, prompt, time, new ShutdownSignal());

        for (var i = 0; i < CrashReporter.MaxDialogsPerWindow; i++)
        {
            reporter.Report("TestSource", Failure);
        }

        Assert.False(reporter.Report("TestSource", Failure));

        time.Advance(CrashReporter.DialogWindow);

        Assert.True(reporter.Report("TestSource", Failure));
    }

    [Fact]
    public void Сбой_самого_окна_не_выпускается_наружу()
    {
        var log = new FakeCrashLog();
        var prompt = new ReentrantUserPrompt(static () => throw new InvalidOperationException("окна нет"));
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        Assert.False(reporter.Report("TestSource", Failure));

        // И предохранитель после сбоя показа не остаётся взведённым навсегда.
        Assert.Single(log.Entries);
        Assert.False(reporter.Report("TestSource", Failure));
        Assert.Equal(2, prompt.Shown);
    }

    [Fact]
    public void Сбой_самого_журнала_не_мешает_показать_окно()
    {
        var log = new FakeCrashLog { Failure = new IOException("диск полон") };
        var prompt = new FakeUserPrompt();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), new ShutdownSignal());

        Assert.True(reporter.Report("TestSource", Failure));
        Assert.Single(prompt.Errors);
    }

    [Fact]
    public void После_начала_гашения_окно_не_показывается_а_сбой_записывается()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var signal = new ShutdownSignal();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), signal);

        signal.MarkStarted();

        // Окно уже спрятано: модальное окно об ошибке всплыло бы поверх пустого места,
        // без владельца, и держало бы процесс живым, пока его не закроют, — тот самый
        // процесс-призрак, который двухфазное закрытие и убирает.
        Assert.False(reporter.Report("TestSource", Failure));
        Assert.Empty(prompt.Errors);

        // Замолчать сбой при этом нельзя: в журнал он попадает как обычно.
        Assert.Equal(("TestSource", Failure), Assert.Single(log.Entries));
    }

    [Fact]
    public void Все_сбои_во_время_гашения_доходят_до_журнала()
    {
        var log = new FakeCrashLog();
        var prompt = new FakeUserPrompt();
        var signal = new ShutdownSignal();
        var reporter = new CrashReporter(log, prompt, new ManualTimeProvider(), signal);

        signal.MarkStarted();

        // Сбоев во время гашения бывает много: завершение каждой псевдоконсоли поднимает
        // своё событие. Тишина на экране не должна превращаться в тишину в журнале —
        // иначе разбираться с гашением будет не по чему.
        for (var i = 0; i < CrashReporter.MaxDialogsPerWindow + 1; i++)
        {
            Assert.False(reporter.Report("TestSource", Failure));
        }

        Assert.Empty(prompt.Errors);
        Assert.Equal(CrashReporter.MaxDialogsPerWindow + 1, log.Entries.Count);
    }

    /// <summary>Окно, которое на показе выполняет заданное действие: повторный сбой или отказ открыться.</summary>
    private sealed class ReentrantUserPrompt(Action onShow) : IUserPrompt
    {
        public int Shown { get; private set; }

        public bool Confirm(string title, string message) => true;

        public void ShowError(string title, string message)
        {
            Shown++;
            onShow();
        }
    }
}
