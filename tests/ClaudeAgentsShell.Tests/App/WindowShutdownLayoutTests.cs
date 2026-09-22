using ClaudeAgentsShell.App;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Порядок закрытия окна относительно раскладки (issue #4): раскладка записывается и
/// замораживается <b>до</b> гашения псевдоконсолей, иначе гашение — выход оболочек и
/// <c>SessionEnd</c> у каждой вкладки — оставило бы на диске пустую раскладку.
/// </summary>
public sealed class WindowShutdownLayoutTests
{
    private sealed class Steps
    {
        private readonly object _sync = new();
        private readonly List<string> _log = [];

        public TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? PersistFailure { get; init; }

        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_sync)
                {
                    return [.. _log];
                }
            }
        }

        public Task PersistAsync()
        {
            Add("persist");
            if (PersistFailure is { } failure)
            {
                throw failure;
            }

            return Persisted.Task;
        }

        public Task ShutdownAsync()
        {
            Add("shutdown");
            return Task.CompletedTask;
        }

        public void Hide() => Add("hide");

        public void Close()
        {
            Add("close");
            Closed.TrySetResult();
        }

        private void Add(string step)
        {
            lock (_sync)
            {
                _log.Add(step);
            }
        }
    }

    private static WindowShutdownSequence Create(Steps steps, FakeCrashLog log) =>
        new(steps.PersistAsync, steps.ShutdownAsync, steps.Hide, steps.Close, new ManualTimeProvider(), new ShutdownSignal(), log);

    [Fact]
    public async Task Раскладка_снимается_внутри_первой_попытки_и_гашение_ждёт_её_записи()
    {
        var steps = new Steps();
        var sequence = Create(steps, new FakeCrashLog());

        Assert.True(sequence.HandleCloseRequest());

        // Снимок сделан синхронно, прямо в попытке закрытия, — раньше, чем окно спрятано
        // и чем очередь интерфейса успела бы выполнить хоть одно событие выхода оболочки.
        Assert.Equal(new[] { "persist", "hide" }, steps.Log);

        // Пока запись не дошла до диска, гашение не начинается.
        await Task.Delay(50);
        Assert.DoesNotContain("shutdown", steps.Log);

        steps.Persisted.SetResult();
        await steps.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "persist", "hide", "shutdown", "close" }, steps.Log);
    }

    [Fact]
    public async Task Сбой_записи_раскладки_уходит_в_журнал_и_гашение_идёт_дальше()
    {
        var steps = new Steps { PersistFailure = new IOException("диск занят") };
        var log = new FakeCrashLog();
        var sequence = Create(steps, log);

        Assert.True(sequence.HandleCloseRequest());
        await steps.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "persist", "hide", "shutdown", "close" }, steps.Log);
        Assert.Equal(WindowShutdownSequence.LayoutCrashSource, Assert.Single(log.Entries).Source);
    }

    [Fact]
    public async Task Повторная_попытка_раскладку_второй_раз_не_пишет()
    {
        var steps = new Steps();
        var sequence = Create(steps, new FakeCrashLog());

        sequence.HandleCloseRequest();
        sequence.HandleCloseRequest();
        steps.Persisted.SetResult();
        await steps.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(steps.Log, static step => step == "persist");
    }
}
