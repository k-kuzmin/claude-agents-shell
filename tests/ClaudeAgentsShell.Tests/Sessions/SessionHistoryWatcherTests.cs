using System.Diagnostics;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Sessions.History;
using ClaudeAgentsShell.Sessions.Storage;
using ClaudeAgentsShell.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class SessionHistoryWatcherTests
{
    private const string WorkingDirectory = @"D:\src\domovoy";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Появление_транскрипта_сообщается()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);
        File.WriteAllText(Path.Combine(history, "a.jsonl"), "{}\n");

        await counter.WaitForAsync(1);
    }

    [Fact]
    public async Task Изменение_и_удаление_транскрипта_сообщаются()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var transcript = Path.Combine(history, "a.jsonl");
        File.WriteAllText(transcript, "{}\n");
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);

        File.AppendAllText(transcript, "{}\n");
        await counter.WaitForAsync(1);

        var afterChange = counter.Count;
        File.Delete(transcript);
        await counter.WaitForAsync(afterChange + 1);
    }

    [Fact]
    public async Task Посторонний_файл_не_мешает_сообщить_о_транскрипте()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);
        File.WriteAllText(Path.Combine(history, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(history, "b.jsonl"), "{}\n");

        await counter.WaitForAsync(1);
    }

    [Fact]
    public async Task Пачка_событий_сворачивается_в_один_вызов()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var counter = new CallCounter();
        var time = new ManualTimeProvider();

        // Время управляемое: пока его не двигают, окно не закрывается, сколько бы ни тянулась
        // доставка событий FileSystemWatcher. Поэтому исход не зависит от нагрузки машины.
        var watcher = new SessionHistoryWatcher(
            new AppDataPaths(temp.Combine("appdata"), ProjectsDirectory(temp)), time, Debounce);
        using var subscription = watcher.Watch(WorkingDirectory, counter.Increment);
        for (var index = 0; index < 20; index++)
        {
            File.WriteAllText(Path.Combine(history, $"s{index}.jsonl"), "{}\n");
        }

        await WaitUntilAsync(() => time.ArmedTimers > 0);
        Assert.Equal(0, counter.Count);

        // Таймер срабатывает внутри Advance, синхронно: сколько событий ни пришло к этому
        // моменту, все они свернулись в один вызов.
        time.Advance(Debounce);
        Assert.Equal(1, counter.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < WaitLimit, "Условие не выполнилось за отведённое время.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Каталог_истории_появившийся_позже_начинает_сообщать()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(ProjectsDirectory(temp));
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);

        // Появление самого каталога — уже изменение списка.
        var history = CreateHistoryDirectory(temp);
        await counter.WaitForAsync(1);

        var afterAppear = counter.Count;
        File.WriteAllText(Path.Combine(history, "late.jsonl"), "{}\n");
        await counter.WaitForAsync(afterAppear + 1);
    }

    [Fact]
    public async Task Путь_созданный_разом_на_несколько_уровней_тоже_замечается()
    {
        using var temp = new TempDirectory();
        var counter = new CallCounter();

        // Нет ни «claude», ни «projects»: наблюдение стоит на корне временного каталога.
        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);

        var history = CreateHistoryDirectory(temp);
        File.WriteAllText(Path.Combine(history, "first.jsonl"), "{}\n");
        await counter.WaitForAsync(1);

        var afterAppear = counter.Count;
        File.WriteAllText(Path.Combine(history, "second.jsonl"), "{}\n");
        await counter.WaitForAsync(afterAppear + 1);
    }

    [Fact]
    public async Task Удалённый_и_пересозданный_каталог_истории_снова_сообщает()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        File.WriteAllText(Path.Combine(history, "a.jsonl"), "{}\n");
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);

        Directory.Delete(history, recursive: true);
        await counter.WaitForAsync(1);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var afterDelete = counter.Count;
        CreateHistoryDirectory(temp);
        File.WriteAllText(Path.Combine(history, "b.jsonl"), "{}\n");
        await counter.WaitForAsync(afterDelete + 1);
    }

    [Fact]
    public async Task Без_каталога_истории_посторонние_подкаталоги_не_сообщаются()
    {
        using var temp = new TempDirectory();
        var projects = ProjectsDirectory(temp);
        Directory.CreateDirectory(projects);
        var counter = new CallCounter();

        using var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);
        Directory.CreateDirectory(Path.Combine(projects, "другой-проект"));

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task После_освобождения_вызовов_нет()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var counter = new CallCounter();

        var subscription = CreateWatcher(temp).Watch(WorkingDirectory, counter.Increment);
        File.WriteAllText(Path.Combine(history, "a.jsonl"), "{}\n");

        // Событие уже в пути, окно взведено — освобождение обязано его отсечь.
        subscription.Dispose();
        var afterDispose = counter.Count;

        File.WriteAllText(Path.Combine(history, "b.jsonl"), "{}\n");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(afterDispose, counter.Count);
    }

    [Fact]
    public async Task Освобождение_дожидается_идущего_вызова()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = false;

        var subscription = CreateWatcher(temp).Watch(WorkingDirectory, () =>
        {
            entered.Set();
            release.Wait(WaitLimit);
            Volatile.Write(ref finished, true);
        });
        File.WriteAllText(Path.Combine(history, "a.jsonl"), "{}\n");
        Assert.True(entered.Wait(WaitLimit));

        var disposing = Task.Run(subscription.Dispose);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(disposing.IsCompleted);

        release.Set();
        await disposing.WaitAsync(WaitLimit);
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public async Task Освобождение_изнутри_обработчика_не_блокируется()
    {
        using var temp = new TempDirectory();
        var history = CreateHistoryDirectory(temp);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;

        subscription = CreateWatcher(temp).Watch(WorkingDirectory, () =>
        {
            Volatile.Read(ref subscription)?.Dispose();
            done.TrySetResult();
        });
        File.WriteAllText(Path.Combine(history, "a.jsonl"), "{}\n");

        await done.Task.WaitAsync(WaitLimit);
    }

    [Fact]
    public void Наблюдатель_зарегистрирован_в_слое_сессий()
    {
        using var provider = new ServiceCollection().AddSessionsLayer().BuildServiceProvider();

        Assert.IsType<SessionHistoryWatcher>(provider.GetRequiredService<ISessionHistoryWatcher>());
    }

    private static SessionHistoryWatcher CreateWatcher(TempDirectory temp, TimeSpan? debounce = null) =>
        new(new AppDataPaths(temp.Combine("appdata"), ProjectsDirectory(temp)), TimeProvider.System, debounce ?? Debounce);

    private static string ProjectsDirectory(TempDirectory temp) => temp.Combine("claude", "projects");

    private static string CreateHistoryDirectory(TempDirectory temp)
    {
        var directory = Path.Combine(ProjectsDirectory(temp), SessionSlug.From(WorkingDirectory));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class CallCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        /// <summary>Ждёт, пока вызовов станет не меньше <paramref name="expected" />, но не дольше предела.</summary>
        public async Task WaitForAsync(int expected)
        {
            var clock = Stopwatch.StartNew();
            while (Count < expected)
            {
                Assert.True(clock.Elapsed < WaitLimit, $"Ожидалось вызовов: {expected}, было: {Count}.");
                await Task.Delay(20);
            }
        }
    }
}
