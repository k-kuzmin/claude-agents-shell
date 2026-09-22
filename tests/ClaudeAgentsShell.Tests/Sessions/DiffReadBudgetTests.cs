using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class DiffReadBudgetTests
{
    [Fact]
    public void Резерв_списывает_только_если_хватает()
    {
        var budget = new DiffReadBudget(100);

        Assert.True(budget.TryReserve(60));
        Assert.False(budget.TryReserve(41));
        Assert.True(budget.TryReserve(40));
        Assert.Equal(0, budget.Remaining);
        Assert.True(budget.TryReserve(0));
    }

    [Fact]
    public void Параллельный_резерв_не_уходит_в_минус()
    {
        var budget = new DiffReadBudget(1000);
        var granted = 0;

        Parallel.For(0, 10_000, _ =>
        {
            if (budget.TryReserve(1))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(1000, granted);
        Assert.Equal(0, budget.Remaining);
    }
}
