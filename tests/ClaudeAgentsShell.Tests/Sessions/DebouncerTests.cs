using ClaudeAgentsShell.Sessions.Git;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class DebouncerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void Пачка_сигналов_склеивается_в_один_вызов()
    {
        var time = new ManualTimeProvider();
        var runs = 0;
        using var debouncer = new Debouncer(Window, _ => Count(ref runs), time);

        // Одна операция git трогает .git несколько раз подряд — это ровно такая пачка.
        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        debouncer.Signal();

        Assert.Equal(0, runs);

        time.Advance(Window);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Следующая_пачка_вызывает_действие_снова()
    {
        var time = new ManualTimeProvider();
        var runs = 0;
        using var debouncer = new Debouncer(Window, _ => Count(ref runs), time);

        debouncer.Signal();
        time.Advance(Window);
        debouncer.Signal();
        time.Advance(Window);

        Assert.Equal(2, runs);
    }

    [Fact]
    public void Без_сигнала_таймер_не_взводится()
    {
        var time = new ManualTimeProvider();
        var runs = 0;
        using var debouncer = new Debouncer(Window, _ => Count(ref runs), time);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, runs);
        Assert.Equal(0, time.ArmedTimers);
    }

    [Fact]
    public void Освобождение_снимает_взведённый_таймер()
    {
        var time = new ManualTimeProvider();
        var runs = 0;
        var debouncer = new Debouncer(Window, _ => Count(ref runs), time);

        debouncer.Signal();
        debouncer.Dispose();
        time.Advance(Window);

        Assert.Equal(0, runs);
    }

    [Fact]
    public void Исключение_действия_не_выходит_наружу()
    {
        var time = new ManualTimeProvider();
        using var debouncer = new Debouncer(Window, _ => throw new InvalidOperationException("ветка не прочиталась"), time);

        debouncer.Signal();

        time.Advance(Window);
    }

    private static Task Count(ref int runs)
    {
        Interlocked.Increment(ref runs);
        return Task.CompletedTask;
    }
}
