using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitProcessTerminationTests
{
    [Fact]
    public void Исключение_снятия_не_всплывает_ни_из_пула_ни_напрямую()
    {
        using var called = new CountdownEvent(2);
        var termination = new GitProcessTermination(() =>
        {
            called.Signal();
            throw new AggregateException(new InvalidOperationException("дерево процессов"));
        });

        termination.KillNow();
        termination.Request();

        // Необработанное исключение в работе пула уронило бы процесс тестов целиком.
        Assert.True(called.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
    }

    [Fact]
    public async Task Close_дожидается_идущего_снятия_и_запрещает_новые()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var termination = new GitProcessTermination(() =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });

        termination.Request();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var close = Task.Run(termination.Close);
        await Task.Delay(100);
        Assert.False(close.IsCompleted);

        release.Set();
        await close.WaitAsync(TimeSpan.FromSeconds(5));

        termination.Request();
        termination.KillNow();
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Request_не_ждёт_снятия()
    {
        using var release = new ManualResetEventSlim();
        var termination = new GitProcessTermination(() => release.Wait(TimeSpan.FromSeconds(10)));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        termination.Request();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        release.Set();
        termination.Close();
    }
}
