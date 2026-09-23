using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.Terminal;

public sealed class BridgeDiffMessageWriterTests
{
    private const int Max = BridgeMessageWriter.MaxDiffMessageLength;
    private static readonly TerminalId Id = new("t1");
    private readonly BridgeMessageWriter _writer = new();

    [Theory]
    [InlineData("diff.pending")]
    [InlineData("diff.stale")]
    public void Простые_сообщения_несут_тип_и_вкладку(string type)
    {
        string json = type switch
        {
            "diff.pending" => _writer.DiffPending(Id),
            _ => _writer.DiffStale(Id),
        };

        using var document = JsonDocument.Parse(json);
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        Assert.Equal("t1", document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void DiffError_без_пути_пишет_null()
    {
        using var document = JsonDocument.Parse(_writer.DiffError(Id, null, "нет git"));
        var root = document.RootElement;

        Assert.Equal("diff.error", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("path").ValueKind);
        Assert.Equal("нет git", root.GetProperty("message").GetString());
    }

    [Fact]
    public void DiffError_у_файла_экранирует_кавычки_и_переводы_строк()
    {
        using var document = JsonDocument.Parse(_writer.DiffError(Id, "src/\"a\".cs", "строка 1\nстрока\t2\u0001"));
        var root = document.RootElement;

        Assert.Equal("src/\"a\".cs", root.GetProperty("path").GetString());
        Assert.Equal("строка 1\nстрока\t2\u0001", root.GetProperty("message").GetString());
    }

    [Fact]
    public void DiffIndex_повторяет_форму_из_контракта()
    {
        var index = new DiffIndex(
            "D:/r",
            "origin/main",
            "abc123",
            IgnoreWhitespace: true,
            [
                new DiffFileEntry("src/a.cs", null, DiffChangeKind.Modified, 12, 3, DiffCollapseReason.None),
                new DiffFileEntry("Папка с пробелом/b.cs", "old/b.cs", DiffChangeKind.Renamed, 0, 0, DiffCollapseReason.LargeDiff),
                new DiffFileEntry("pic.png", null, DiffChangeKind.Added, null, null, DiffCollapseReason.Binary),
                new DiffFileEntry("yarn.lock", null, DiffChangeKind.Deleted, 1, 900, DiffCollapseReason.Generated),
                new DiffFileEntry("new.txt", null, DiffChangeKind.Untracked, 5, 0, DiffCollapseReason.None),
            ]);
        GitWorktree[] worktrees = [new("D:/r", "feat/x", true), new("D:/r2", null, false)];

        using var document = JsonDocument.Parse(_writer.DiffIndex(Id, index, worktrees, "смотри сюда", ["src/a.cs"]));
        var root = document.RootElement;

        Assert.Equal("diff.index", root.GetProperty("type").GetString());
        Assert.Equal("D:/r", root.GetProperty("root").GetString());
        Assert.Equal("origin/main", root.GetProperty("base").GetString());
        Assert.Equal("abc123", root.GetProperty("mergeBase").GetString());
        Assert.True(root.GetProperty("ws").GetBoolean());
        Assert.Equal("смотри сюда", root.GetProperty("note").GetString());
        Assert.Equal("src/a.cs", root.GetProperty("expand")[0].GetString());

        var trees = root.GetProperty("worktrees");
        Assert.Equal(2, trees.GetArrayLength());
        Assert.Equal("feat/x", trees[0].GetProperty("branch").GetString());
        Assert.True(trees[0].GetProperty("current").GetBoolean());
        Assert.Equal(JsonValueKind.Null, trees[1].GetProperty("branch").ValueKind);

        var files = root.GetProperty("files");
        Assert.Equal(5, files.GetArrayLength());
        Assert.Equal(["M", "R", "A", "D", "U"], files.EnumerateArray().Select(f => f.GetProperty("k").GetString()));
        Assert.Equal(["none", "large", "binary", "generated", "none"], files.EnumerateArray().Select(f => f.GetProperty("c").GetString()));
        Assert.Equal("Папка с пробелом/b.cs", files[1].GetProperty("p").GetString());
        Assert.Equal("old/b.cs", files[1].GetProperty("o").GetString());
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("o").ValueKind);
        Assert.Equal(12, files[0].GetProperty("a").GetInt32());
        Assert.Equal(3, files[0].GetProperty("d").GetInt32());
        Assert.Equal(JsonValueKind.Null, files[2].GetProperty("a").ValueKind);
        Assert.Equal(JsonValueKind.Null, files[2].GetProperty("d").ValueKind);
    }

    [Fact]
    public void DiffIndex_без_note_и_деревьев()
    {
        var index = new DiffIndex("D:/r", "main", "abc", false, []);

        using var document = JsonDocument.Parse(_writer.DiffIndex(Id, index, [], null, []));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
        Assert.Equal(0, root.GetProperty("worktrees").GetArrayLength());
        Assert.Equal(0, root.GetProperty("files").GetArrayLength());
        Assert.False(root.GetProperty("ws").GetBoolean());
    }

    [Fact]
    public void DiffIndex_не_экранирует_кириллицу()
    {
        var index = new DiffIndex("D:/р", "main", "abc", false, []);

        Assert.Contains("D:/р", _writer.DiffIndex(Id, index, [], null, []), StringComparison.Ordinal);
    }

    [Fact]
    public void DiffFile_пустой_текст_одна_последняя_часть()
    {
        string single = Assert.Single(_writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.Hunks, string.Empty, false)));

        using var document = JsonDocument.Parse(single);
        var root = document.RootElement;
        Assert.Equal("diff.file", root.GetProperty("type").GetString());
        Assert.Equal("a.cs", root.GetProperty("path").GetString());
        Assert.Equal("hunks", root.GetProperty("ctx").GetString());
        Assert.Equal(0, root.GetProperty("part").GetInt32());
        Assert.True(root.GetProperty("last").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("text").GetString());
    }

    [Fact]
    public void DiffFile_весь_файл_и_обрезка_попадают_в_сообщение()
    {
        string single = Assert.Single(_writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.FullFile, "x", true)));

        using var document = JsonDocument.Parse(single);
        Assert.Equal("full", document.RootElement.GetProperty("ctx").GetString());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void DiffFile_ровно_на_потолке_одна_часть_символ_сверху_две()
    {
        int overhead = Overhead("a.cs");
        string exact = new('a', Max - overhead);

        string single = Assert.Single(_writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.Hunks, exact, false)));
        Assert.Equal(Max, single.Length);

        var parts = _writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.Hunks, exact + "b", false)).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, part => Assert.True(part.Length <= Max));
        Assert.Equal(exact + "b", Reassemble(parts));
    }

    [Fact]
    public void DiffFile_крупный_текст_режется_по_потолку_с_номерами_и_признаком_последней()
    {
        var text = new StringBuilder();
        for (int i = 0; text.Length < (3 * Max) + 1000; i++)
        {
            text.Append("+строка \"").Append(i).Append("\"\t\\ рамка ─┼─ 😀\r\n");
        }

        string original = text.ToString();
        var parts = _writer.DiffFile(Id, new FileDiff("src/a.cs", DiffContext.FullFile, original, false)).ToList();

        Assert.True(parts.Count >= 3);
        for (int i = 0; i < parts.Count; i++)
        {
            Assert.True(parts[i].Length <= Max, $"часть {i}: {parts[i].Length}");

            using var document = JsonDocument.Parse(parts[i]);
            Assert.Equal(i, document.RootElement.GetProperty("part").GetInt32());
            Assert.Equal(i == parts.Count - 1, document.RootElement.GetProperty("last").GetBoolean());
        }

        Assert.Equal(original, Reassemble(parts));
    }

    [Fact]
    public void DiffFile_не_рвёт_суррогатную_пару_на_границе()
    {
        int overhead = Overhead("a.cs");

        // Непоследняя часть на символ длиннее конверта последней ("false" против "true"),
        // поэтому под текст остаётся Max - overhead - 1: граница приходится ровно между
        // половинами эмодзи, а весь текст длиннее потолка.
        string head = new('a', Max - overhead - 2);
        string original = head + "😀" + new string('b', 100);

        var parts = _writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.Hunks, original, false)).ToList();

        Assert.Equal(2, parts.Count);
        Assert.All(parts, part => Assert.True(part.Length <= Max));

        var texts = parts.Select(Text).ToList();
        Assert.Equal(head, texts[0]);
        Assert.False(char.IsHighSurrogate(texts[0][^1]));
        Assert.False(char.IsLowSurrogate(texts[1][0]));
        Assert.Equal(original, string.Concat(texts));
    }

    [Fact]
    public void DiffFile_одиночный_суррогат_заменяется_а_не_роняет()
    {
        string single = Assert.Single(_writer.DiffFile(Id, new FileDiff("a.cs", DiffContext.Hunks, "a\ud800b", false)));

        Assert.Equal("a\ufffdb", Text(single));
    }

    [Fact]
    public void DiffFile_проверяет_аргумент_сразу()
    {
        Assert.Throws<ArgumentNullException>(() => _writer.DiffFile(Id, null!));
    }

    /// <summary>Длина конверта одной последней части без текста — ровно то, что не текст.</summary>
    private int Overhead(string path) =>
        _writer.DiffFile(Id, new FileDiff(path, DiffContext.Hunks, string.Empty, false)).Single().Length;

    private static string Text(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString()!;
    }

    private static string Reassemble(IEnumerable<string> parts) => string.Concat(parts.Select(Text));
}
