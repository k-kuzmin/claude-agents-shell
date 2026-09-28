using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Files;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>Чтение файлов для <c>show_file</c>: корень, границы корня, размер, двоичность, кодировка.</summary>
public sealed class WorkspaceFileReaderTests
{
    private static readonly GitDiffOptions Options = new() { FileOutputCeilingBytes = 1024, BinarySniffBytes = 64 };

    [Fact]
    public async Task Корень_верх_репозитория_по_каталогу_git()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("repo", ".git"));
        var nested = Directory.CreateDirectory(temp.Combine("repo", "src", "app")).FullName;

        var root = await CreateReader().ResolveRootAsync(nested, CancellationToken.None);

        Assert.Equal(temp.Combine("repo"), root);
    }

    [Fact]
    public async Task Корень_worktree_по_файлу_git()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/wt\n");
        var nested = Directory.CreateDirectory(Path.Combine(worktree, "src")).FullName;

        Assert.Equal(worktree, await CreateReader().ResolveRootAsync(nested + Path.DirectorySeparatorChar, CancellationToken.None));
    }

    [Fact]
    public async Task Вне_репозитория_корень_сам_каталог_а_нет_каталога_null()
    {
        using var temp = new TempDirectory();
        var plain = Directory.CreateDirectory(temp.Combine("plain")).FullName;
        var reader = CreateReader();

        // Выше временной папки может найтись чужой .git (домашний каталог под git) — тогда
        // корнем законно становится он; ожидание подстраивается, поведение продукта — нет.
        Assert.Equal(EnclosingWorkTree(plain) ?? plain, await reader.ResolveRootAsync(plain, CancellationToken.None));
        Assert.Null(await reader.ResolveRootAsync(temp.Combine("missing"), CancellationToken.None));
        Assert.Null(await reader.ResolveRootAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task Относительный_и_абсолютный_путь_внутри_корня_читаются_с_фокусом_как_пришёл()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        Directory.CreateDirectory(temp.Combine("src"));
        File.WriteAllText(temp.Combine("src", "a.cs"), "class A {}\n// Привет\n", new UTF8Encoding(false));
        var reader = CreateReader();
        var focus = new LineRange(2, 99);

        var relative = await reader.ReadAsync(root, temp.Combine("src"), new ShowFileItem("a.cs", focus), CancellationToken.None);
        var absolute = await reader.ReadAsync(root, root, new ShowFileItem(temp.Combine("src", "a.cs"), null), CancellationToken.None);
        var dotted = await reader.ReadAsync(root, temp.Combine("src"), new ShowFileItem("../src/./a.cs", null), CancellationToken.None);

        Assert.Equal(new ViewedFile("src/a.cs", "class A {}\n// Привет\n", focus, ViewedFileProblem.None), relative);
        Assert.Equal(new ViewedFile("src/a.cs", "class A {}\n// Привет\n", null, ViewedFileProblem.None), absolute);
        Assert.Equal("src/a.cs", dotted.Path);
        Assert.Equal(ViewedFileProblem.None, dotted.Problem);
    }

    [Fact]
    public async Task Путь_за_корень_через_точки_или_абсолютный_не_читается()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(temp.Combine("repo")).FullName;
        File.WriteAllText(temp.Combine("secret.txt"), "secret");
        File.WriteAllText(temp.Combine("repo2.txt"), "neighbour");
        var reader = CreateReader();

        var dotted = await reader.ReadAsync(root, root, new ShowFileItem("../secret.txt", null), CancellationToken.None);
        var absolute = await reader.ReadAsync(root, root, new ShowFileItem(temp.Combine("secret.txt"), null), CancellationToken.None);

        // Сосед с тем же префиксом имени — не внутри корня.
        var prefix = await reader.ReadAsync(root, root, new ShowFileItem("../repo2.txt", null), CancellationToken.None);

        Assert.All([dotted, absolute, prefix], static file =>
        {
            Assert.Equal(ViewedFileProblem.OutsideRoot, file.Problem);
            Assert.Null(file.Text);
        });
    }

    [Fact]
    public async Task Нет_файла_или_это_каталог_NotFound()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("dir"));
        var reader = CreateReader();

        var missing = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("nope.cs", null), CancellationToken.None);
        var directory = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("dir", null), CancellationToken.None);
        var self = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem(".", null), CancellationToken.None);

        Assert.Equal(new ViewedFile("nope.cs", null, null, ViewedFileProblem.NotFound), missing);
        Assert.Equal(ViewedFileProblem.NotFound, directory.Problem);
        Assert.Equal(ViewedFileProblem.NotFound, self.Problem);
    }

    [Fact]
    public async Task Больше_потолка_TooLarge_ровно_потолок_читается()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("big.txt"), Enumerable.Repeat((byte)'a', Options.FileOutputCeilingBytes + 1).ToArray());
        File.WriteAllBytes(temp.Combine("edge.txt"), Enumerable.Repeat((byte)'b', Options.FileOutputCeilingBytes).ToArray());
        var reader = CreateReader();

        var big = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("big.txt", null), CancellationToken.None);
        var edge = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("edge.txt", null), CancellationToken.None);

        Assert.Equal(ViewedFileProblem.TooLarge, big.Problem);
        Assert.Null(big.Text);
        Assert.Equal(ViewedFileProblem.None, edge.Problem);
        Assert.Equal(Options.FileOutputCeilingBytes, edge.Text!.Length);
    }

    [Fact]
    public async Task NUL_в_начале_Binary_а_после_окна_проверки_текст()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("bin.dat"), [0x41, 0x00, 0x42]);
        var late = Enumerable.Repeat((byte)'x', Options.BinarySniffBytes).Append((byte)0).ToArray();
        File.WriteAllBytes(temp.Combine("late.txt"), late);
        var reader = CreateReader();

        var binary = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("bin.dat", null), CancellationToken.None);
        var text = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem("late.txt", null), CancellationToken.None);

        Assert.Equal(ViewedFileProblem.Binary, binary.Problem);
        Assert.Null(binary.Text);
        Assert.Equal(ViewedFileProblem.None, text.Problem);
    }

    [Fact]
    public async Task BOM_определяет_кодировку_и_в_текст_не_попадает()
    {
        using var temp = new TempDirectory();
        const string content = "Привет, ╔═╗\n";
        File.WriteAllText(temp.Combine("utf8.txt"), content, new UTF8Encoding(true));
        File.WriteAllText(temp.Combine("utf16.txt"), content, Encoding.Unicode);
        File.WriteAllText(temp.Combine("utf16be.txt"), content, Encoding.BigEndianUnicode);
        var reader = CreateReader();

        foreach (var name in new[] { "utf8.txt", "utf16.txt", "utf16be.txt" })
        {
            var file = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem(name, null), CancellationToken.None);
            Assert.Equal(ViewedFileProblem.None, file.Problem);
            Assert.Equal(content, file.Text);
        }
    }

    [Fact]
    public async Task Невалидный_UTF8_заменяется_а_не_роняет()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("bad.txt"), [0x61, 0xC3, 0x28, 0x62]);

        var file = await CreateReader().ReadAsync(temp.Path, temp.Path, new ShowFileItem("bad.txt", null), CancellationToken.None);

        Assert.Equal(ViewedFileProblem.None, file.Problem);
        Assert.Equal("a�(b", file.Text);
    }

    [LinkFact]
    public async Task Ссылка_или_junction_наружу_OutsideRoot_а_внутрь_читается()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(temp.Combine("repo")).FullName;
        var outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        File.WriteAllText(Path.Combine(root, "inner", "ok.txt"), "ok");
        var reader = CreateReader();

        var fileLink = LinkFactAttribute.TryCreateFileSymlink(Path.Combine(root, "ln.txt"), Path.Combine(outside, "secret.txt"));
        var junction = LinkFactAttribute.TryCreateJunction(Path.Combine(root, "jn"), outside);
        var innerJunction = LinkFactAttribute.TryCreateJunction(Path.Combine(root, "jin"), Path.Combine(root, "inner"));
        Assert.True(fileLink || junction, "Не создалась ни одна ссылка: LinkFact должен был пропустить тест");

        if (fileLink)
        {
            var file = await reader.ReadAsync(root, root, new ShowFileItem("ln.txt", null), CancellationToken.None);
            Assert.Equal(ViewedFileProblem.OutsideRoot, file.Problem);
            Assert.Null(file.Text);
        }

        if (junction)
        {
            var file = await reader.ReadAsync(root, root, new ShowFileItem("jn/secret.txt", null), CancellationToken.None);
            Assert.Equal(ViewedFileProblem.OutsideRoot, file.Problem);
            Assert.Null(file.Text);
        }

        if (innerJunction)
        {
            var file = await reader.ReadAsync(root, root, new ShowFileItem("jin/ok.txt", null), CancellationToken.None);
            Assert.Equal(new ViewedFile("jin/ok.txt", "ok", null, ViewedFileProblem.None), file);
        }
    }

    [Theory]
    [InlineData(@"\\host\share\a.cs")]
    [InlineData("//host/share/a.cs")]
    [InlineData(@"\\?\C:\a.cs")]
    [InlineData(@"\\.\C:\a.cs")]
    [InlineData(@"\??\C:\a.cs")]
    [InlineData(@"\??\UNC\host\share\a.cs")]
    [InlineData(@"\foo")]
    [InlineData("/foo")]
    [InlineData("C:foo")]
    [InlineData("a:b")]
    [InlineData(@"C:\a.cs:stream")]
    public async Task Нелокальные_пути_не_доходят_до_диска(string path)
    {
        using var temp = new TempDirectory();
        var reader = CreateReader();

        var asFile = await reader.ReadAsync(temp.Path, temp.Path, new ShowFileItem(path, null), CancellationToken.None);
        var asDirectory = await reader.ReadAsync(temp.Path, path, new ShowFileItem("a.cs", null), CancellationToken.None);
        var asRoot = await reader.ReadAsync(path, temp.Path, new ShowFileItem("a.cs", null), CancellationToken.None);

        Assert.Equal(ViewedFileProblem.OutsideRoot, asFile.Problem);
        Assert.Equal(ViewedFileProblem.NotFound, asDirectory.Problem);
        Assert.Equal(ViewedFileProblem.NotFound, asRoot.Problem);
        Assert.Null(await reader.ResolveRootAsync(path, CancellationToken.None));
    }

    [Fact]
    public void Предел_размера_берётся_из_порога_diff()
    {
        Assert.Equal(Options.FileOutputCeilingBytes, CreateReader().MaxFileBytes);
    }

    private static string? EnclosingWorkTree(string directory)
    {
        for (var current = Path.GetDirectoryName(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            var entry = Path.Combine(current, ".git");
            if (Directory.Exists(entry) || File.Exists(entry))
            {
                return current;
            }
        }

        return null;
    }

    private static WorkspaceFileReader CreateReader() => new(Options);
}
