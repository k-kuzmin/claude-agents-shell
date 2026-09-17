using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.Protocol;

public sealed class BridgeMessageWriterTests
{
    private static readonly TerminalId Id = new("t1");
    private readonly BridgeMessageWriter _writer = new();

    [Fact]
    public void Out_собирает_сообщение_протокола()
    {
        byte[] payload = [0x01, 0x02, 0x03];

        using var document = JsonDocument.Parse(_writer.Out(Id, payload));
        var root = document.RootElement;

        Assert.Equal("out", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal(payload, Convert.FromBase64String(root.GetProperty("b64").GetString()!));
    }

    [Fact]
    public void Out_с_пустой_пачкой_остаётся_валидным_json()
    {
        using var document = JsonDocument.Parse(_writer.Out(Id, ReadOnlySpan<byte>.Empty));

        Assert.Equal(string.Empty, document.RootElement.GetProperty("b64").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(1023)]
    [InlineData(64 * 1024)]
    public void Out_переживает_любую_длину_пачки(int length)
    {
        byte[] payload = new byte[length];
        Random.Shared.NextBytes(payload);

        using var document = JsonDocument.Parse(_writer.Out(Id, payload));

        Assert.Equal(payload, Convert.FromBase64String(document.RootElement.GetProperty("b64").GetString()!));
    }

    [Fact]
    public void Create_экранирует_заголовок()
    {
        string message = _writer.Create(Id, "Домовой \"кавычки\" \\ и перевод\nстроки");

        using var document = JsonDocument.Parse(message);
        Assert.Equal("create", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("Домовой \"кавычки\" \\ и перевод\nстроки", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Show_close_exited_соответствуют_протоколу()
    {
        using var show = JsonDocument.Parse(_writer.Show(Id));
        using var close = JsonDocument.Parse(_writer.Close(Id));
        using var exited = JsonDocument.Parse(_writer.Exited(Id, -1));

        Assert.Equal("show", show.RootElement.GetProperty("type").GetString());
        Assert.Equal("close", close.RootElement.GetProperty("type").GetString());
        Assert.Equal("exited", exited.RootElement.GetProperty("type").GetString());
        Assert.Equal(-1, exited.RootElement.GetProperty("code").GetInt32());
    }
}

public sealed class Utf8ChunkBoundaryTests
{
    private const string Sample =
        "Привет, мир! ┌─────┐ │ рамка │ └─────┘ 🚀 работает — ёжик 🇷🇺 ✅ 中文";

    private static readonly TerminalId Id = new("t1");

    /// <summary>
    /// Разрез на любой позиции, включая середину многобайтовой последовательности:
    /// байты обязаны дойти до страницы без потерь, а склеенный поток — раскодироваться
    /// в исходную строку. Отдельная пачка при этом валидным UTF-8 быть не обязана.
    /// </summary>
    [Fact]
    public void Разрез_на_любой_границе_не_ломает_поток()
    {
        byte[] source = Encoding.UTF8.GetBytes(Sample);
        var writer = new BridgeMessageWriter();

        for (int cut = 0; cut <= source.Length; cut++)
        {
            byte[] restored =
            [
                .. ExtractPayload(writer.Out(Id, source.AsSpan(0, cut))),
                .. ExtractPayload(writer.Out(Id, source.AsSpan(cut))),
            ];

            Assert.True(source.AsSpan().SequenceEqual(restored), $"Разрез на позиции {cut} потерял байты.");
            Assert.Equal(Sample, Encoding.UTF8.GetString(restored));
        }
    }

    [Fact]
    public void Разрез_на_три_части_не_ломает_поток()
    {
        byte[] source = Encoding.UTF8.GetBytes(Sample);
        var writer = new BridgeMessageWriter();

        for (int first = 0; first <= source.Length; first++)
        {
            for (int second = first; second <= source.Length; second += 7)
            {
                byte[] restored =
                [
                    .. ExtractPayload(writer.Out(Id, source.AsSpan(0, first))),
                    .. ExtractPayload(writer.Out(Id, source.AsSpan(first, second - first))),
                    .. ExtractPayload(writer.Out(Id, source.AsSpan(second))),
                ];

                Assert.Equal(Sample, Encoding.UTF8.GetString(restored));
            }
        }
    }

    private static byte[] ExtractPayload(string message)
    {
        using var document = JsonDocument.Parse(message);
        return Convert.FromBase64String(document.RootElement.GetProperty("b64").GetString()!);
    }
}

public sealed class BridgeMessageParserTests
{
    private readonly BridgeMessageParser _parser = new();

    [Fact]
    public void Разбирает_ввод()
    {
        byte[] data = Encoding.UTF8.GetBytes("привет 🚀");
        string json = $$"""{"type":"in","id":"t1","b64":"{{Convert.ToBase64String(data)}}"}""";

        Assert.True(_parser.TryParse(json, out var message));

        var input = Assert.IsType<InboundBridgeMessage.Input>(message);
        Assert.Equal("t1", input.TerminalId.Value);
        Assert.Equal(data, input.Data.ToArray());
    }

    [Fact]
    public void Разбирает_размер()
    {
        Assert.True(_parser.TryParse("""{"type":"resize","id":"t1","cols":120,"rows":34}""", out var message));

        var resize = Assert.IsType<InboundBridgeMessage.Resize>(message);
        Assert.Equal(new TerminalSize(120, 34), resize.Size);
    }

    /// <summary>
    /// Нулевой размер разбирается штатно: отбрасывать его — обязанность вызывающего
    /// (<c>IPtySession.Resize</c>), парсер тут ничего не решает.
    /// </summary>
    [Fact]
    public void Нулевой_размер_разбирается_но_невалиден()
    {
        Assert.True(_parser.TryParse("""{"type":"resize","id":"t1","cols":0,"rows":0}""", out var message));

        var resize = Assert.IsType<InboundBridgeMessage.Resize>(message);
        Assert.False(resize.Size.IsValid);
    }

    [Fact]
    public void Разбирает_готовность()
    {
        Assert.True(_parser.TryParse("""{"type":"ready","id":"t7"}""", out var message));

        Assert.Equal("t7", Assert.IsType<InboundBridgeMessage.Ready>(message).TerminalId.Value);
    }

    [Fact]
    public void Разбирает_подтверждение_записи()
    {
        Assert.True(_parser.TryParse("""{"type":"ack","id":"t1","bytes":4096}""", out var message));

        Assert.Equal(4096, Assert.IsType<InboundBridgeMessage.Ack>(message).Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не json вовсе")]
    [InlineData("{\"type\":\"in\",\"id\":\"t1\"")]
    [InlineData("[1,2,3]")]
    [InlineData("\"строка\"")]
    [InlineData("""{"type":"неизвестный","id":"t1"}""")]
    [InlineData("""{"id":"t1","b64":"AA=="}""")]
    [InlineData("""{"type":"in","b64":"AA=="}""")]
    [InlineData("""{"type":"in","id":"t1"}""")]
    [InlineData("""{"type":"in","id":"t1","b64":"не base64"}""")]
    [InlineData("""{"type":"in","id":"  ","b64":"AA=="}""")]
    [InlineData("""{"type":"in","id":123,"b64":"AA=="}""")]
    [InlineData("""{"type":"resize","id":"t1","cols":120}""")]
    [InlineData("""{"type":"resize","id":"t1","cols":"120","rows":"34"}""")]
    public void Мусор_не_роняет_разбор(string json)
    {
        Assert.False(_parser.TryParse(json, out var message));
        Assert.Null(message);
    }
}
