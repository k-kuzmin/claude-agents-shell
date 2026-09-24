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

        using var document = JsonDocument.Parse(_writer.Out(Id, 42, payload));
        var root = document.RootElement;

        Assert.Equal("out", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal(42, root.GetProperty("seq").GetInt64());
        Assert.Equal(payload, Convert.FromBase64String(root.GetProperty("b64").GetString()!));
    }

    [Fact]
    public void Out_с_пустой_пачкой_остаётся_валидным_json()
    {
        using var document = JsonDocument.Parse(_writer.Out(Id, 0, ReadOnlySpan<byte>.Empty));

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

        using var document = JsonDocument.Parse(_writer.Out(Id, 7, payload));

        Assert.Equal(payload, Convert.FromBase64String(document.RootElement.GetProperty("b64").GetString()!));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(9L)]
    [InlineData(10L)]
    [InlineData(999_999L)]
    [InlineData(long.MaxValue)]
    public void Номер_пачки_любой_длины_не_ломает_сообщение(long sequence)
    {
        byte[] payload = [1, 2, 3, 4, 5];

        using var document = JsonDocument.Parse(_writer.Out(Id, sequence, payload));

        Assert.Equal(sequence, document.RootElement.GetProperty("seq").GetInt64());
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

    [Fact]
    public void PasteResult_text_экранирует_обратные_слеши_и_кириллицу()
    {
        const string text = @"""C:\Папка с пробелом\a.png"" D:\b.txt";

        string json = _writer.PasteResult(Id, new PasteContent.Text(text));

        Assert.Contains(@"C:\\Папка с пробелом\\a.png", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("paste.result", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal("text", root.GetProperty("kind").GetString());
        Assert.Equal(text, root.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("image")]
    [InlineData("none")]
    public void PasteResult_без_текста_не_несёт_поля_text(string kind)
    {
        PasteContent content = kind == "image" ? new PasteContent.Image() : new PasteContent.None();

        using var document = JsonDocument.Parse(_writer.PasteResult(Id, content));
        var root = document.RootElement;

        Assert.Equal("paste.result", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal(kind, root.GetProperty("kind").GetString());
        Assert.False(root.TryGetProperty("text", out _));
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
                .. ExtractPayload(writer.Out(Id, 0, source.AsSpan(0, cut))),
                .. ExtractPayload(writer.Out(Id, 1, source.AsSpan(cut))),
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
                    .. ExtractPayload(writer.Out(Id, 0, source.AsSpan(0, first))),
                    .. ExtractPayload(writer.Out(Id, 1, source.AsSpan(first, second - first))),
                    .. ExtractPayload(writer.Out(Id, 2, source.AsSpan(second))),
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
    public void Разбирает_запрос_вставки()
    {
        Assert.True(_parser.TryParse("""{"type":"paste.request","id":"t3"}""", out var message));

        Assert.Equal("t3", Assert.IsType<InboundBridgeMessage.PasteRequest>(message).TerminalId.Value);
    }

    [Fact]
    public void Бросок_файлов_разбирается_с_пустым_списком_путей()
    {
        Assert.True(_parser.TryParse("""{"type":"drop","id":"t4"}""", out var message));

        var dropped = Assert.IsType<InboundBridgeMessage.FilesDropped>(message);
        Assert.Equal("t4", dropped.TerminalId.Value);
        Assert.Empty(dropped.Paths);
    }

    [Theory]
    [InlineData("""{"type":"paste.request"}""")]
    [InlineData("""{"type":"paste.request","id":""}""")]
    [InlineData("""{"type":"drop"}""")]
    [InlineData("""{"type":"drop","id":" "}""")]
    public void Вставка_без_вкладки_игнорируется(string json)
    {
        Assert.False(_parser.TryParse(json, out var message));
        Assert.Null(message);
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
        Assert.True(_parser.TryParse("""{"type":"ack","id":"t1","seq":17,"bytes":4096}""", out var message));

        var ack = Assert.IsType<InboundBridgeMessage.Ack>(message);
        Assert.Equal(17, ack.Sequence);
        Assert.Equal(4096, ack.Bytes);
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
    [InlineData("""{"type":"ack","id":"t1","bytes":10}""")]
    [InlineData("""{"type":"ack","id":"t1","seq":-1}""")]
    [InlineData("""{"type":"ack","id":"t1","seq":"17"}""")]
    public void Мусор_не_роняет_разбор(string json)
    {
        Assert.False(_parser.TryParse(json, out var message));
        Assert.Null(message);
    }
}
