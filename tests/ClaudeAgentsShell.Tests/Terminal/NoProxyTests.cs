using ClaudeAgentsShell.Terminal;
using Xunit;

namespace ClaudeAgentsShell.Tests.Terminal;

public sealed class NoProxyTests
{
    [Theory]
    [InlineData(null, "127.0.0.1,localhost")]
    [InlineData("", "127.0.0.1,localhost")]
    [InlineData("corp.local", "corp.local,127.0.0.1,localhost")]
    [InlineData("corp.local,", "corp.local,127.0.0.1,localhost")]
    public void LoopbackIsAppendedWithoutLosingTheExistingValue(string? existing, string expected) =>
        Assert.Equal(expected, TerminalWorkspace.LoopbackBypassingProxy(existing));
}
