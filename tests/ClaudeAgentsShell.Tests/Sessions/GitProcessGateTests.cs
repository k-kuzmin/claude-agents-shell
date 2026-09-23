using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitProcessGateTests
{
    [Fact]
    public async Task Пускает_не_больше_ёмкости()
    {
        var gate = new GitProcessGate(2);

        using var a = await gate.EnterAsync(CancellationToken.None);
        using var b = await gate.EnterAsync(CancellationToken.None);
        var c = gate.EnterAsync(CancellationToken.None);

        Assert.False(c.IsCompleted);
        Assert.Equal(1, gate.Waiting);
        a.Dispose();
        using var entered = await c.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task Очередь_честная()
    {
        var gate = new GitProcessGate(1);
        var holder = await gate.EnterAsync(CancellationToken.None);
        var order = new List<int>();
        var waiters = Enumerable.Range(0, 5)
            .Select(i => Enter(gate, i, order))
            .ToList();

        holder.Dispose();
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public async Task Отмена_ожидания_убирает_из_очереди_и_не_занимает_места()
    {
        var gate = new GitProcessGate(1);
        var holder = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cancellation.Token);
        var next = gate.EnterAsync(CancellationToken.None);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, gate.Waiting);

        holder.Dispose();
        using var entered = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task Отменённый_заранее_токен_не_ждёт()
    {
        var gate = new GitProcessGate(1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cancellation.Token));
        using var entered = await gate.EnterAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Повторное_освобождение_не_добавляет_места()
    {
        var gate = new GitProcessGate(1);
        var lease = await gate.EnterAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        using var a = await gate.EnterAsync(CancellationToken.None);
        Assert.False(gate.EnterAsync(CancellationToken.None).IsCompleted);
    }

    private static async Task Enter(GitProcessGate gate, int index, List<int> order)
    {
        using var lease = await gate.EnterAsync(CancellationToken.None);
        lock (order)
        {
            order.Add(index);
        }
    }
}
