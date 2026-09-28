using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.Terminal;

public sealed class BridgeFileMessageWriterTests
{
    private const int Max = BridgeMessageWriter.MaxDiffMessageLength;
    private static readonly TerminalId Id = new("t1");
    private readonly BridgeMessageWriter _writer = new();

    [Fact]
    public void FileShow_повторяет_форму_из_контракта()
    {
        var set = new FileViewSet(
            "D:/r",
            "Смотри сюда",
            [
                new ViewedFile("src/a.cs", "x", new LineRange(10, 20), ViewedFileProblem.None),
                new ViewedFile("b.txt", "y", null, ViewedFileProblem.None),
            ]);

        using var document = JsonDocument.Parse(_writer.FileShow(Id, 7, set));
        var root = document.RootElement;

        Assert.Equal("file.show", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal(7, root.GetProperty("seq").GetInt64());
        Assert.Equal("D:/r", root.GetProperty("root").GetString());
        Assert.Equal("Смотри сюда", root.GetProperty("note").GetString());

        var files = root.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal("src/a.cs", files[0].GetProperty("p").GetString());
        Assert.Equal(10, files[0].GetProperty("focus").GetProperty("from").GetInt32());
        Assert.Equal(20, files[0].GetProperty("focus").GetProperty("to").GetInt32());
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("problem").ValueKind);
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("focus").ValueKind);

        // Текст в file.show не попадает: он идёт частями file.content.
        Assert.False(files[0].TryGetProperty("text", out _));
    }

    [Theory]
    [InlineData(ViewedFileProblem.NotFound, "notFound")]
    [InlineData(ViewedFileProblem.OutsideRoot, "outsideRoot")]
    [InlineData(ViewedFileProblem.TooLarge, "tooLarge")]
    [InlineData(ViewedFileProblem.Binary, "binary")]
    [InlineData(ViewedFileProblem.Unreadable, "unreadable")]
    public void FileShow_причина_кодом(ViewedFileProblem problem, string code)
    {
        var file = new ViewedFile("a.bin", null, null, problem);
        using var document = JsonDocument.Parse(_writer.FileShow(Id, 1, new FileViewSet("r", null, [file])));

        Assert.Equal(code, document.RootElement.GetProperty("files")[0].GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("note").ValueKind);
        Assert.False(BridgeMessageWriter.HasContent(file));
    }

    [Fact]
    public void FileShow_без_текста_и_без_причины_показывается_нечитаемым()
    {
        // Нарушение контракта ViewedFile не должно оставлять «Загрузка…» навсегда.
        var file = new ViewedFile("a.cs", null, null, ViewedFileProblem.None);
        using var document = JsonDocument.Parse(_writer.FileShow(Id, 1, new FileViewSet("r", null, [file])));

        Assert.Equal("unreadable", document.RootElement.GetProperty("files")[0].GetProperty("problem").GetString());
        Assert.False(BridgeMessageWriter.HasContent(file));
        Assert.True(BridgeMessageWriter.HasContent(new ViewedFile("a.cs", string.Empty, null, ViewedFileProblem.None)));
    }

    [Fact]
    public void FileShow_экранирует_пути_и_не_экранирует_кириллицу()
    {
        var set = new FileViewSet("C:\\р", "\"кавычки\"\n", [new ViewedFile("папка/ф\"айл.cs", "", null, ViewedFileProblem.None)]);
        string json = _writer.FileShow(Id, 1, set);

        Assert.Contains("папка", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("C:\\р", document.RootElement.GetProperty("root").GetString());
        Assert.Equal("\"кавычки\"\n", document.RootElement.GetProperty("note").GetString());
        Assert.Equal("папка/ф\"айл.cs", document.RootElement.GetProperty("files")[0].GetProperty("p").GetString());
    }

    [Fact]
    public void FileContent_пустой_текст_одна_последняя_часть()
    {
        string single = Assert.Single(_writer.FileContent(Id, 3, 2, string.Empty));

        using var document = JsonDocument.Parse(single);
        var root = document.RootElement;
        Assert.Equal("file.content", root.GetProperty("type").GetString());
        Assert.Equal("t1", root.GetProperty("id").GetString());
        Assert.Equal(3, root.GetProperty("seq").GetInt64());
        Assert.Equal(2, root.GetProperty("i").GetInt32());
        Assert.Equal(0, root.GetProperty("part").GetInt32());
        Assert.True(root.GetProperty("last").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("text").GetString());
        Assert.False(root.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void FileContent_ровно_на_потолке_одна_часть_символ_сверху_две()
    {
        int overhead = Assert.Single(_writer.FileContent(Id, 1, 0, string.Empty)).Length;
        string exact = new('a', Max - overhead);

        string single = Assert.Single(_writer.FileContent(Id, 1, 0, exact));
        Assert.Equal(Max, single.Length);

        var parts = _writer.FileContent(Id, 1, 0, exact + "b").ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, part => Assert.True(part.Length <= Max));
        Assert.Equal(exact + "b", Reassemble(parts));
    }

    [Fact]
    public void FileContent_крупный_текст_режется_с_номерами_частей_и_тем_же_seq()
    {
        var text = new StringBuilder();
        for (int i = 0; text.Length < (2 * Max) + 1000; i++)
        {
            text.Append("строка \"").Append(i).Append("\"\t\\ рамка ─┼─ 😀\r\n");
        }

        string original = text.ToString();
        var parts = _writer.FileContent(Id, 42, 5, original).ToList();

        Assert.True(parts.Count >= 2);
        for (int i = 0; i < parts.Count; i++)
        {
            Assert.True(parts[i].Length <= Max, $"часть {i}: {parts[i].Length}");

            using var document = JsonDocument.Parse(parts[i]);
            Assert.Equal(42, document.RootElement.GetProperty("seq").GetInt64());
            Assert.Equal(5, document.RootElement.GetProperty("i").GetInt32());
            Assert.Equal(i, document.RootElement.GetProperty("part").GetInt32());
            Assert.Equal(i == parts.Count - 1, document.RootElement.GetProperty("last").GetBoolean());
        }

        Assert.Equal(original, Reassemble(parts));
    }

    [Fact]
    public void FileContent_не_рвёт_суррогатную_пару_на_границе()
    {
        int overhead = Assert.Single(_writer.FileContent(Id, 1, 0, string.Empty)).Length;
        string head = new('a', Max - overhead - 2);
        string original = head + "😀" + new string('b', 100);

        var texts = _writer.FileContent(Id, 1, 0, original).Select(Text).ToList();

        Assert.Equal(2, texts.Count);
        Assert.Equal(head, texts[0]);
        Assert.Equal(original, string.Concat(texts));
    }

    [Fact]
    public void FileContent_проверяет_аргументы_сразу()
    {
        Assert.Throws<ArgumentNullException>(() => _writer.FileContent(Id, 1, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => _writer.FileContent(Id, 1, -1, "x"));
        Assert.Throws<ArgumentNullException>(() => _writer.FileShow(Id, 1, null!));
    }

    private static string Text(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString()!;
    }

    private static string Reassemble(IEnumerable<string> parts) => string.Concat(parts.Select(Text));
}
