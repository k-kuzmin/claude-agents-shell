using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.Domain;

public sealed class TerminalSizeTests
{
    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, -1)]
    [InlineData(1001, 24)]
    public void Невалидный_размер_отбрасывается(int cols, int rows)
    {
        Assert.False(new TerminalSize(cols, rows).IsValid);
    }

    [Fact]
    public void Размер_по_умолчанию_валиден()
    {
        Assert.True(TerminalSize.Default.IsValid);
    }
}

public sealed class TerminalIdTests
{
    [Fact]
    public void Пустой_идентификатор_не_создаётся()
    {
        Assert.Throws<ArgumentException>(() => new TerminalId("  "));
    }

    [Fact]
    public void Новые_идентификаторы_различаются()
    {
        Assert.NotEqual(TerminalId.New(), TerminalId.New());
    }

    [Fact]
    public void Равенство_по_значению()
    {
        Assert.Equal(new TerminalId("t1"), new TerminalId("t1"));
    }
}
