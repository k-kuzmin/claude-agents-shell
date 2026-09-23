using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitDiffOutputParserTests
{
    [Fact]
    public void Raw_и_numstat_сводятся_в_одну_строку_на_файл()
    {
        var output =
            ":100644 100644 aaa bbb M\0src/a.cs\0"
            + ":000000 100644 000 ccc A\0новый файл.txt\0"
            + ":100644 000000 ddd 000 D\0old.txt\0"
            + ":100644 100644 eee fff R087\0было имя.txt\0стало имя.txt\0"
            + ":100644 100644 ggg hhh M\0bin.dat\0"
            + "12\t3\tsrc/a.cs\0"
            + "1\t0\tновый файл.txt\0"
            + "0\t4\told.txt\0"
            + "2\t1\t\0было имя.txt\0стало имя.txt\0"
            + "-\t-\tbin.dat\0";

        var entries = GitDiffOutputParser.ParseRawNumstat(output);

        Assert.Equal(
            [
                new DiffFileEntry("src/a.cs", null, DiffChangeKind.Modified, 12, 3, DiffCollapseReason.None),
                new DiffFileEntry("новый файл.txt", null, DiffChangeKind.Added, 1, 0, DiffCollapseReason.None),
                new DiffFileEntry("old.txt", null, DiffChangeKind.Deleted, 0, 4, DiffCollapseReason.None),
                new DiffFileEntry("стало имя.txt", "было имя.txt", DiffChangeKind.Renamed, 2, 1, DiffCollapseReason.None),
                new DiffFileEntry("bin.dat", null, DiffChangeKind.Modified, null, null, DiffCollapseReason.None),
            ],
            entries);
    }

    [Fact]
    public void Неслитый_путь_даёт_одну_строку()
    {
        var output = ":000000 000000 000 000 U\0a.txt\0:100644 100644 aaa bbb M\0a.txt\01\t1\ta.txt\0";

        var entry = Assert.Single(GitDiffOutputParser.ParseRawNumstat(output));
        Assert.Equal(DiffChangeKind.Modified, entry.Kind);
        Assert.Equal(1, entry.AddedLines);
    }

    [Fact]
    public void Без_записи_numstat_строк_ноль_а_не_бинарный()
    {
        var entry = Assert.Single(GitDiffOutputParser.ParseRawNumstat(":100644 100644 aaa bbb M\0a.txt\0"));

        Assert.Equal((0, 0), (entry.AddedLines, entry.DeletedLines));
    }

    [Fact]
    public void Пустой_и_оборванный_вывод_не_роняют_разбор()
    {
        Assert.Empty(GitDiffOutputParser.ParseRawNumstat(string.Empty));
        Assert.Empty(GitDiffOutputParser.ParseRawNumstat(":100644 100644 aaa bbb R100\0только старый\0"));
    }

    [Fact]
    public void SplitNul_не_даёт_пустого_хвоста()
    {
        Assert.Equal(["a b", "в г"], GitDiffOutputParser.SplitNul("a b\0в г\0"));
        Assert.Empty(GitDiffOutputParser.SplitNul(string.Empty));
    }

    [Fact]
    public void CheckAttr_собирает_оба_атрибута_на_путь()
    {
        var output =
            "gen/a.cs\0linguist-generated\0set\0gen/a.cs\0diff\0unspecified\0"
            + "b.bin\0linguist-generated\0unspecified\0b.bin\0diff\0unset\0";

        var attributes = GitDiffOutputParser.ParseCheckAttr(output);

        Assert.Equal(new GitPathAttributes("set", null), attributes["gen/a.cs"]);
        Assert.Equal(new GitPathAttributes(null, "unset"), attributes["b.bin"]);
    }

    [Theory]
    [InlineData("refs/heads/main\0\nrefs/heads/master\0\nrefs/remotes/origin/HEAD\0refs/remotes/origin/develop\n", "origin/develop", "refs/remotes/origin/develop")]
    [InlineData("refs/heads/main\0\nrefs/heads/master\0\n", "main", "refs/heads/main")]
    [InlineData("refs/heads/master\0\n", "master", "refs/heads/master")]
    public void База_выбирается_по_порядку(string output, string name, string revision)
    {
        Assert.Equal(new GitBaseCandidate(name, revision), GitDiffOutputParser.PickDefaultBase(output));
    }

    [Fact]
    public void Базы_нет_если_совпадения_только_по_префиксу()
    {
        Assert.Null(GitDiffOutputParser.PickDefaultBase("refs/heads/main/feature\0\n"));
        Assert.Null(GitDiffOutputParser.PickDefaultBase(string.Empty));
    }

    [Fact]
    public void Worktree_list_разбирается_с_z_и_без()
    {
        const string records =
            "worktree D:/r/main\nHEAD 111\nbranch refs/heads/main\n\n"
            + "worktree D:/r/копия с пробелом\nHEAD 222\ndetached\n\n"
            + "worktree D:/r/bare\nbare\n\n"
            + "worktree D:/r/gone\nHEAD 333\nbranch refs/heads/x\nprunable gitdir file points to non-existent location\n\n";

        foreach (var output in new[] { records, records.Replace('\n', '\0') })
        {
            var worktrees = GitDiffOutputParser.ParseWorktreeList(output, @"D:\r\копия с пробелом");

            Assert.Equal(
                [
                    new GitWorktree(Path.GetFullPath("D:/r/main"), "main", false),
                    new GitWorktree(Path.GetFullPath("D:/r/копия с пробелом"), null, true),
                ],
                worktrees);
        }
    }

    [Fact]
    public void Numstat_без_raw_читает_счётчики_бинарный_и_переименование()
    {
        var counts = GitDiffOutputParser.ParseNumstat("3\t1\ta.txt\0-\t-\tbin.dat\0" + "2\t0\t\0old.txt\0новое.txt\0");

        Assert.Equal((3, 1), counts["a.txt"]);
        Assert.Equal((null, null), counts["bin.dat"]);
        Assert.Equal((2, 0), counts["новое.txt"]);
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public void Счётчики_и_бинарность_берутся_из_numstat_с_w_а_файл_без_записи_получает_ноль()
    {
        DiffFileEntry[] entries =
        [
            new("a.txt", null, DiffChangeKind.Modified, 0, 0, DiffCollapseReason.None),
            new("spaces.txt", null, DiffChangeKind.Modified, 0, 0, DiffCollapseReason.None),
            new("bin.dat", null, DiffChangeKind.Modified, 0, 0, DiffCollapseReason.None),
        ];

        var result = GitDiffOutputParser.WithWhitespaceIgnoredCounts(entries, GitDiffOutputParser.ParseNumstat("4\t1\ta.txt\0-\t-\tbin.dat\0"));

        Assert.Equal(["a.txt", "spaces.txt", "bin.dat"], result.Select(static e => e.Path));
        Assert.Equal((4, 1), (result[0].AddedLines, result[0].DeletedLines));
        Assert.Equal((0, 0), (result[1].AddedLines, result[1].DeletedLines));
        Assert.Equal((null, null), (result[2].AddedLines, result[2].DeletedLines));
    }
}
