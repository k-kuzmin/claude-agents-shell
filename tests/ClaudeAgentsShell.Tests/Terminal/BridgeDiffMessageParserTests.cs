using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.Terminal;

public sealed class BridgeDiffMessageParserTests
{
    private readonly BridgeMessageParser _parser = new();

    [Fact]
    public void DiffRefresh_с_null_значит_прежние_дерево_и_базу()
    {
        var message = Parse("""{"type":"diff.refresh","id":"t1","dir":null,"base":null,"ws":true}""");

        var refresh = Assert.IsType<InboundBridgeMessage.DiffRefresh>(message);
        Assert.Equal(new TerminalId("t1"), refresh.TerminalId);
        Assert.Null(refresh.Directory);
        Assert.Null(refresh.BaseRef);
        Assert.True(refresh.IgnoreWhitespace);
    }

    [Fact]
    public void DiffRefresh_без_полей_dir_и_base_тоже_прежние()
    {
        var refresh = Assert.IsType<InboundBridgeMessage.DiffRefresh>(
            Parse("""{"type":"diff.refresh","id":"t1","ws":false}"""));

        Assert.Null(refresh.Directory);
        Assert.Null(refresh.BaseRef);
        Assert.False(refresh.IgnoreWhitespace);
    }

    [Fact]
    public void DiffRefresh_с_другим_деревом_и_базой()
    {
        var refresh = Assert.IsType<InboundBridgeMessage.DiffRefresh>(
            Parse("""{"type":"diff.refresh","id":"t1","dir":"D:/Проект 2","base":"develop","ws":false}"""));

        Assert.Equal("D:/Проект 2", refresh.Directory);
        Assert.Equal("develop", refresh.BaseRef);
    }

    [Theory]
    [InlineData("""{"type":"diff.refresh","id":"t1","dir":null,"base":null}""")]
    [InlineData("""{"type":"diff.refresh","id":"t1","ws":"yes"}""")]
    [InlineData("""{"type":"diff.refresh","id":"t1","dir":5,"ws":false}""")]
    [InlineData("""{"type":"diff.refresh","id":"t1","base":[],"ws":false}""")]
    [InlineData("""{"type":"diff.refresh","ws":false}""")]
    public void DiffRefresh_битое_игнорируется(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData("hunks", DiffContext.Hunks)]
    [InlineData("full", DiffContext.FullFile)]
    public void DiffFileRequest_разбирает_контекст(string ctx, DiffContext expected)
    {
        var request = Assert.IsType<InboundBridgeMessage.DiffFileRequest>(
            Parse($$"""{"type":"diff.file.request","id":"t1","path":"src/а б.cs","ctx":"{{ctx}}"}"""));

        Assert.Equal("src/а б.cs", request.Path);
        Assert.Equal(expected, request.Context);
    }

    [Theory]
    [InlineData("""{"type":"diff.file.request","id":"t1","path":"a.cs","ctx":"all"}""")]
    [InlineData("""{"type":"diff.file.request","id":"t1","path":"a.cs"}""")]
    [InlineData("""{"type":"diff.file.request","id":"t1","path":"","ctx":"hunks"}""")]
    [InlineData("""{"type":"diff.file.request","id":"t1","path":null,"ctx":"hunks"}""")]
    [InlineData("""{"type":"diff.file.request","id":"t1","ctx":"hunks"}""")]
    public void DiffFileRequest_битое_игнорируется(string json) => Assert.Null(Parse(json));

    [Fact]
    public void DiffClosed_разбирается()
    {
        var closed = Assert.IsType<InboundBridgeMessage.DiffClosed>(Parse("""{"type":"diff.closed","id":"t7"}"""));

        Assert.Equal(new TerminalId("t7"), closed.TerminalId);
    }

    [Theory]
    [InlineData("""{"type":"diff.closed"}""")]
    [InlineData("""{"type":"diff.closed","id":""}""")]
    [InlineData("""{"type":"diff.unknown","id":"t1"}""")]
    [InlineData("""{"type":"diff.index","id":"t1"}""")]
    public void Незнакомое_и_без_вкладки_игнорируется(string json) => Assert.Null(Parse(json));

    private InboundBridgeMessage? Parse(string json) =>
        _parser.TryParse(json, out var message) ? message : null;
}
