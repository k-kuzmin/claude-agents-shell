using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitDiffReaderTests
{
    private static GitDiffReader CreateReader(GitDiffOptions? options = null)
    {
        options ??= new GitDiffOptions();
        return new GitDiffReader(options, new GitProcessRunner(options, new GitProcessGate(options.MaxConcurrentProcesses)), new DiffCollapsePolicy(options));
    }

    private static DiffRequest Request(string directory, string? baseRef = null, bool ignoreWhitespace = false, params string[] files) =>
        new(directory, baseRef, files, ignoreWhitespace);

    /// <summary>main с тремя файлами и ветка feature, от которой считается diff.</summary>
    private static GitDiffTestRepository CreateFeatureBranch(string initialBranch = "main")
    {
        var repository = GitDiffTestRepository.Create(initialBranch);
        repository.Write("src/a.cs", "one\ntwo\nthree\n");
        repository.Write("удалить.txt", "x\n");
        repository.Write("rename me.txt", string.Join('\n', Enumerable.Range(1, 40)) + "\n");
        repository.WriteBytes("bin.dat", [0, 1, 2, 3]);
        repository.CommitAll("base");
        repository.Git("checkout", "-q", "-b", "feature");
        return repository;
    }

    [Fact]
    public async Task Оглавление_видит_коммиты_ветки_незакоммиченное_и_новые_файлы()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("src/a.cs", "one\nTWO\nthree\nfour\n");
        repository.CommitAll("feature commit");
        File.Delete(repository.Combine("удалить.txt"));
        repository.Git("mv", "rename me.txt", "новое имя.txt");
        repository.WriteBytes("bin.dat", [0, 9, 9, 9]);
        repository.Write("папка/новый файл.txt", "a\nb\nc");
        repository.Write("добавлен.txt", "совсем другое содержимое\n");
        repository.Git("add", "добавлен.txt");

        var index = await CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None);

        Assert.Equal("main", index.BaseRef);
        Assert.Equal(Path.GetFullPath(repository.Root), index.RepositoryRoot);
        Assert.Equal(repository.Git("rev-parse", "main").Trim(), index.MergeBase);
        var files = index.Files.ToDictionary(static f => f.Path);
        Assert.Equal(new DiffFileEntry("src/a.cs", null, DiffChangeKind.Modified, 2, 1, DiffCollapseReason.None), files["src/a.cs"]);
        Assert.Equal(DiffChangeKind.Deleted, files["удалить.txt"].Kind);
        Assert.Equal(new DiffFileEntry("новое имя.txt", "rename me.txt", DiffChangeKind.Renamed, 0, 0, DiffCollapseReason.None), files["новое имя.txt"]);
        Assert.Equal(new DiffFileEntry("bin.dat", null, DiffChangeKind.Modified, null, null, DiffCollapseReason.Binary), files["bin.dat"]);
        Assert.Equal(new DiffFileEntry("папка/новый файл.txt", null, DiffChangeKind.Untracked, 3, 0, DiffCollapseReason.None), files["папка/новый файл.txt"]);
        Assert.Equal(DiffChangeKind.Added, files["добавлен.txt"].Kind);
        Assert.Equal(6, index.Files.Count);
    }

    [Fact]
    public async Task Оглавление_не_трогает_индекс()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("src/a.cs", "changed\n");
        repository.Write("new.txt", "n\n");
        var indexFile = Path.Combine(repository.Root, ".git", "index");
        var before = File.ReadAllBytes(indexFile);
        var writtenBefore = File.GetLastWriteTimeUtc(indexFile);

        var index = await CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None);
        await CreateReader().ReadFileDiffAsync(index, index.Files[0], DiffContext.Hunks, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(indexFile));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(indexFile));
        Assert.Contains("?? new.txt", repository.Git("status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Из_подкаталога_корень_находится()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("src/a.cs", "changed\n");

        var index = await CreateReader().ListChangesAsync(Request(repository.Combine("src")), CancellationToken.None);

        Assert.Equal(Path.GetFullPath(repository.Root), index.RepositoryRoot);
        Assert.Equal("src/a.cs", Assert.Single(index.Files).Path);
    }

    [Fact]
    public async Task База_origin_HEAD_важнее_main()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("src/a.cs", "on develop\n");
        repository.CommitAll("develop");
        var develop = repository.Git("rev-parse", "HEAD").Trim();
        repository.Git("update-ref", "refs/remotes/origin/develop", develop);
        repository.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/develop");
        repository.Write("x.txt", "x\n");

        var index = await CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None);

        Assert.Equal("origin/develop", index.BaseRef);
        Assert.Equal(develop, index.MergeBase);
        Assert.Equal("x.txt", Assert.Single(index.Files).Path);
    }

    [Fact]
    public async Task Без_main_база_master()
    {
        using var repository = CreateFeatureBranch("master");
        repository.Write("src/a.cs", "changed\n");

        var index = await CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None);

        Assert.Equal("master", index.BaseRef);
    }

    [Fact]
    public async Task Без_origin_HEAD_main_и_master_базы_нет()
    {
        using var repository = CreateFeatureBranch("trunk");

        var failure = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None));

        Assert.Equal(DiffFailure.NoBase, failure.Failure);
    }

    [Fact]
    public async Task Явная_база_используется_как_названа_а_несуществующая_даёт_NoBase()
    {
        using var repository = CreateFeatureBranch("trunk");
        repository.Write("src/a.cs", "changed\n");
        var reader = CreateReader();

        var index = await reader.ListChangesAsync(Request(repository.Root, baseRef: "trunk"), CancellationToken.None);
        Assert.Equal("trunk", index.BaseRef);

        var missing = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(Request(repository.Root, baseRef: "nope"), CancellationToken.None));
        Assert.Equal(DiffFailure.NoBase, missing.Failure);

        var option = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(Request(repository.Root, baseRef: "--all"), CancellationToken.None));
        Assert.Equal(DiffFailure.NoBase, option.Failure);
    }

    [Fact]
    public async Task Не_репозиторий_и_нет_каталога()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader();

        var notRepository = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(Request(temp.Path), CancellationToken.None));
        Assert.Equal(DiffFailure.NotARepository, notRepository.Failure);

        var missing = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(Request(temp.Combine("нет")), CancellationToken.None));
        Assert.Equal(DiffFailure.NotARepository, missing.Failure);
    }

    [Fact]
    public async Task Нет_git()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(new GitDiffOptions { GitExecutable = "cas-no-such-git-" + Guid.NewGuid().ToString("N") });

        var failure = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(Request(temp.Path), CancellationToken.None));

        Assert.Equal(DiffFailure.GitNotFound, failure.Failure);
    }

    [Fact]
    public async Task Свёртка_порог_встроенный_список_и_gitattributes()
    {
        using var repository = CreateFeatureBranch();
        repository.Write(".gitattributes", "gen/** linguist-generated\n*.snap -diff\n");
        repository.CommitAll("attributes");
        repository.Write("big.txt", string.Join('\n', Enumerable.Range(0, 401)) + "\n");
        repository.Write("package-lock.json", "{}\n");
        repository.Write("gen/model.cs", "class A {}\n");
        repository.Write("test.snap", "snapshot\n");
        repository.Write("small.cs", "ok\n");

        var index = await CreateReader().ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var collapse = index.Files.ToDictionary(static f => f.Path, static f => f.Collapse);

        Assert.Equal(DiffCollapseReason.LargeDiff, collapse["big.txt"]);
        Assert.Equal(DiffCollapseReason.Generated, collapse["package-lock.json"]);
        Assert.Equal(DiffCollapseReason.Generated, collapse["gen/model.cs"]);
        Assert.Equal(DiffCollapseReason.Generated, collapse["test.snap"]);
        Assert.Equal(DiffCollapseReason.None, collapse["small.cs"]);
        Assert.Equal(DiffCollapseReason.None, collapse[".gitattributes"]);
    }

    [Fact]
    public async Task Files_сужают_оглавление_и_не_сворачиваются()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("package-lock.json", "{}\n");
        repository.Write("папка/файл с пробелом.txt", string.Join('\n', Enumerable.Range(0, 500)) + "\n");
        repository.Write("other.cs", "x\n");
        repository.Write("src/a.cs", "changed\n");
        repository.WriteBytes("bin.dat", [0, 7]);

        var index = await CreateReader().ListChangesAsync(
            Request(repository.Root, null, false, "package-lock.json", @"папка\файл с пробелом.txt", repository.Combine("src/a.cs"), "./bin.dat"),
            CancellationToken.None);

        var collapse = index.Files.ToDictionary(static f => f.Path, static f => f.Collapse);
        Assert.Equal(4, collapse.Count);
        Assert.Equal(DiffCollapseReason.None, collapse["package-lock.json"]);
        Assert.Equal(DiffCollapseReason.None, collapse["папка/файл с пробелом.txt"]);
        Assert.Equal(DiffCollapseReason.None, collapse["src/a.cs"]);
        Assert.Equal(DiffCollapseReason.None, collapse["bin.dat"]);
    }

    [Fact]
    public async Task Неотслеживаемые_бинарный_и_сверх_потолка()
    {
        using var repository = CreateFeatureBranch();
        var binary = new byte[100];
        binary[50] = 0;
        Array.Fill(binary, (byte)'a', 0, 50);
        repository.WriteBytes("image.bin", binary);
        repository.Write("huge.txt", new string('x', 2000) + "\n");
        repository.Write("empty.txt", string.Empty);

        var options = new GitDiffOptions { FileOutputCeilingBytes = 1000 };
        var index = await CreateReader(options).ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var files = index.Files.ToDictionary(static f => f.Path);

        Assert.Equal(new DiffFileEntry("image.bin", null, DiffChangeKind.Untracked, null, null, DiffCollapseReason.Binary), files["image.bin"]);
        Assert.Equal(new DiffFileEntry("huge.txt", null, DiffChangeKind.Untracked, null, 0, DiffCollapseReason.LargeDiff), files["huge.txt"]);
        Assert.Equal(new DiffFileEntry("empty.txt", null, DiffChangeKind.Untracked, 0, 0, DiffCollapseReason.None), files["empty.txt"]);
    }

    [Fact]
    public async Task Сверх_общего_бюджета_строки_не_считаются_а_бинарный_остаётся_бинарным()
    {
        using var repository = CreateFeatureBranch();
        for (var i = 0; i < 10; i++)
        {
            repository.Write($"new/f{i}.txt", string.Concat(Enumerable.Repeat("0123456789\n", 10)));
        }

        var binary = new byte[200];
        repository.WriteBytes("new/z.bin", binary);

        // Бюджет на три текстовых файла по 110 байт; бинарный в него уже не влезает.
        var options = new GitDiffOptions { UntrackedCountBudgetBytes = 330 };
        var index = await CreateReader(options).ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var texts = index.Files.Where(static f => f.Path.EndsWith(".txt", StringComparison.Ordinal)).ToList();

        Assert.Equal(10, texts.Count);
        Assert.True(texts.Count(static f => f.AddedLines == 10) <= 3);
        Assert.True(texts.Count(static f => f.AddedLines is null) >= 7);
        Assert.All(texts.Where(static f => f.AddedLines is null), static f => Assert.Equal(DiffCollapseReason.LargeDiff, f.Collapse));
        Assert.Equal(DiffCollapseReason.Binary, index.Files.Single(static f => f.Path == "new/z.bin").Collapse);
    }

    [Fact]
    public async Task Diff_файла_hunks_и_весь_файл()
    {
        using var repository = GitDiffTestRepository.Create();
        repository.Write("long.txt", string.Join('\n', Enumerable.Range(1, 30)) + "\n");
        repository.CommitAll("long");
        repository.Git("checkout", "-q", "-b", "feature");
        repository.Write("long.txt", string.Join('\n', Enumerable.Range(1, 30).Select(static n => n == 15 ? "fifteen" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n");
        var reader = CreateReader();
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var entry = index.Files.Single(static f => f.Path == "long.txt");

        var hunks = await reader.ReadFileDiffAsync(index, entry, DiffContext.Hunks, CancellationToken.None);
        var full = await reader.ReadFileDiffAsync(index, entry, DiffContext.FullFile, CancellationToken.None);

        Assert.False(hunks.Truncated);
        Assert.Contains("--- a/long.txt", hunks.Text, StringComparison.Ordinal);
        Assert.Contains("+fifteen", hunks.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n 1\n", hunks.Text, StringComparison.Ordinal);
        Assert.Equal(DiffContext.FullFile, full.Context);
        Assert.Contains("\n 1\n", full.Text, StringComparison.Ordinal);
        Assert.Contains("\n 30\n", full.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Переименование_с_кириллицей_читается_по_обоим_путям()
    {
        using var repository = CreateFeatureBranch();
        repository.Git("mv", "rename me.txt", "новое имя.txt");
        File.AppendAllText(repository.Combine("новое имя.txt"), "добавка\n");
        var reader = CreateReader();
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var entry = Assert.Single(index.Files);

        var diff = await reader.ReadFileDiffAsync(index, entry, DiffContext.Hunks, CancellationToken.None);

        Assert.Equal(DiffChangeKind.Renamed, entry.Kind);
        Assert.Contains("rename from rename me.txt", diff.Text, StringComparison.Ordinal);
        Assert.Contains("rename to новое имя.txt", diff.Text, StringComparison.Ordinal);
        Assert.Contains("+добавка", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Флаг_w_убирает_изменения_пробелов()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("src/a.cs", "one\n  two\nthree\n");
        var reader = CreateReader();

        var plain = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var ignoring = await reader.ListChangesAsync(Request(repository.Root, ignoreWhitespace: true), CancellationToken.None);

        Assert.Equal(1, Assert.Single(plain.Files).AddedLines);
        var entry = Assert.Single(ignoring.Files);
        Assert.True(ignoring.IgnoreWhitespace);
        Assert.Equal(0, entry.AddedLines);
        var diff = await reader.ReadFileDiffAsync(ignoring, entry, DiffContext.Hunks, CancellationToken.None);
        Assert.DoesNotContain("+  two", diff.Text, StringComparison.Ordinal);
        var withSpaces = await reader.ReadFileDiffAsync(plain, plain.Files[0], DiffContext.Hunks, CancellationToken.None);
        Assert.Contains("+  two", withSpaces.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Потолок_обрывает_diff_по_целой_строке()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("big.txt", string.Join('\n', Enumerable.Range(0, 5000).Select(static n => "line " + n)) + "\n");
        repository.CommitAll("big");
        repository.Write("big.txt", string.Join('\n', Enumerable.Range(0, 5000).Select(static n => "changed " + n)) + "\n");
        var options = new GitDiffOptions { FileOutputCeilingBytes = 4096 };
        var reader = CreateReader(options);
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);

        var diff = await reader.ReadFileDiffAsync(index, Assert.Single(index.Files), DiffContext.Hunks, CancellationToken.None);

        Assert.True(diff.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(diff.Text) <= options.FileOutputCeilingBytes);
        Assert.EndsWith("\n", diff.Text, StringComparison.Ordinal);
        Assert.StartsWith("diff --git", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Потолок_по_умолчанию_4_МБ()
    {
        using var repository = CreateFeatureBranch();
        var line = new string('z', 1023) + "\n";
        repository.Write("scene.unity", string.Concat(Enumerable.Repeat(line, 5 * 1024)));
        repository.Git("add", "scene.unity");
        var reader = CreateReader();
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);

        var diff = await reader.ReadFileDiffAsync(index, Assert.Single(index.Files), DiffContext.Hunks, CancellationToken.None);

        Assert.Equal(4 * 1024 * 1024, new GitDiffOptions().FileOutputCeilingBytes);
        Assert.True(diff.Truncated);
        Assert.True(diff.Text.Length <= 4 * 1024 * 1024);
    }

    [Fact]
    public async Task Неотслеживаемый_файл_собирается_как_новый()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("папка/новый файл.txt", "первая\nвторая");
        repository.Write("one.txt", "single\n");
        var reader = CreateReader();
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var files = index.Files.ToDictionary(static f => f.Path);

        var diff = await reader.ReadFileDiffAsync(index, files["папка/новый файл.txt"], DiffContext.Hunks, CancellationToken.None);
        var single = await reader.ReadFileDiffAsync(index, files["one.txt"], DiffContext.FullFile, CancellationToken.None);

        Assert.Equal(
            "diff --git a/папка/новый файл.txt b/папка/новый файл.txt\nnew file mode 100644\n--- /dev/null\n+++ b/папка/новый файл.txt\n"
            + "@@ -0,0 +1,2 @@\n+первая\n+вторая\n\\ No newline at end of file\n",
            diff.Text);
        Assert.False(diff.Truncated);
        Assert.Contains("@@ -0,0 +1 @@\n+single\n", single.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Неотслеживаемый_сверх_потолка_обрезан()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("huge.txt", string.Concat(Enumerable.Repeat("0123456789\n", 1000)));
        var options = new GitDiffOptions { FileOutputCeilingBytes = 105 };
        var reader = CreateReader(options);
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);

        var diff = await reader.ReadFileDiffAsync(index, Assert.Single(index.Files), DiffContext.Hunks, CancellationToken.None);

        Assert.True(diff.Truncated);
        Assert.Contains("@@ -0,0 +1,9 @@\n", diff.Text, StringComparison.Ordinal);
        Assert.EndsWith("+0123456789\n", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Путь_вне_репозитория_не_читается()
    {
        using var repository = CreateFeatureBranch();
        repository.Write("x.txt", "x\n");
        var reader = CreateReader();
        var index = await reader.ListChangesAsync(Request(repository.Root), CancellationToken.None);
        var escape = new DiffFileEntry("../secret.txt", null, DiffChangeKind.Untracked, 1, 0, DiffCollapseReason.None);

        await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ReadFileDiffAsync(index, escape, DiffContext.Hunks, CancellationToken.None));
    }

    [Fact]
    public async Task Отменённый_запрос_бросает_OperationCanceled()
    {
        using var repository = CreateFeatureBranch();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateReader().ListChangesAsync(Request(repository.Root), cancelled.Token));
    }

    [Fact]
    public async Task Worktree_list_отмечает_текущее_дерево()
    {
        using var repository = CreateFeatureBranch();
        var linked = Path.Combine(repository.Container, "копия worktree");
        repository.Git("worktree", "add", "-q", "-b", "agent", linked);

        var worktrees = await CreateReader().ListWorktreesAsync(linked, CancellationToken.None);

        Assert.Equal(2, worktrees.Count);
        Assert.Contains(new GitWorktree(Path.GetFullPath(repository.Root), "feature", false), worktrees);
        Assert.Contains(new GitWorktree(Path.GetFullPath(linked), "agent", true), worktrees);
    }

    [Fact]
    public async Task Worktree_list_вне_репозитория_пуст()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await CreateReader().ListWorktreesAsync(temp.Path, CancellationToken.None));
        Assert.Empty(await CreateReader().ListWorktreesAsync(temp.Combine("нет"), CancellationToken.None));
    }
}
