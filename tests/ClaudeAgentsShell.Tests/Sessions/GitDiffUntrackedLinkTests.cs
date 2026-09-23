using System.Diagnostics;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Неотслеживаемые ссылки (issue #5): цель ссылки наружу не открывается, как у git —
/// ссылка сама изменённый объект.
/// </summary>
public sealed class GitDiffUntrackedLinkTests
{
    private const string Secret = "СЕКРЕТ вне репозитория";

    [LinkFact]
    public async Task Ссылки_наружу_не_раскрывают_чужое_содержимое()
    {
        using var repository = GitDiffTestRepository.Create();
        repository.Write("tracked.txt", "x\n");
        repository.CommitAll("base");
        var (outside, secretFile) = CreateOutside(repository);

        var fileLink = LinkFactAttribute.TryCreateFileSymlink(repository.Combine("ln.txt"), secretFile);
        var directoryLink = LinkFactAttribute.TryCreateDirectorySymlink(repository.Combine("dln"), outside);
        var junction = LinkFactAttribute.TryCreateJunction(repository.Combine("jn"), outside);
        Assert.True(fileLink || junction, "Не создалась ни одна ссылка: LinkFact должен был пропустить тест");

        var reader = CreateReader();
        var index = await reader.ListChangesAsync(new DiffRequest(repository.Root, null, [], false), CancellationToken.None);
        var files = index.Files.ToDictionary(static f => f.Path);

        if (fileLink)
        {
            var link = files["ln.txt"];
            Assert.Equal((1, 0), (link.AddedLines, link.DeletedLines));
            var diff = await reader.ReadFileDiffAsync(index, link, DiffContext.Hunks, CancellationToken.None);
            Assert.Contains("new file mode 120000", diff.Text, StringComparison.Ordinal);
            Assert.Contains("\n+" + secretFile.Replace('\\', '/') + "\n\\ No newline at end of file\n", diff.Text, StringComparison.Ordinal);
        }

        if (directoryLink)
        {
            // git выдаёт symlink на каталог одной записью — это сама ссылка.
            var link = files["dln"];
            Assert.Equal((1, 0), (link.AddedLines, link.DeletedLines));
            var diff = await reader.ReadFileDiffAsync(index, link, DiffContext.Hunks, CancellationToken.None);
            Assert.Contains("+" + outside.Replace('\\', '/'), diff.Text, StringComparison.Ordinal);
        }

        if (junction)
        {
            // git перечисляет файлы за junction как свои; читать их нельзя.
            var behind = files["jn/secret.txt"];
            Assert.Equal((null, 0), (behind.AddedLines, behind.DeletedLines));
            var failure = await Assert.ThrowsAsync<DiffUnavailableException>(
                () => reader.ReadFileDiffAsync(index, behind, DiffContext.Hunks, CancellationToken.None));
            Assert.Contains("через ссылку", failure.Message, StringComparison.Ordinal);
        }

        foreach (var entry in index.Files)
        {
            try
            {
                var diff = await reader.ReadFileDiffAsync(index, entry, DiffContext.Hunks, CancellationToken.None);
                Assert.DoesNotContain(Secret, diff.Text, StringComparison.Ordinal);
            }
            catch (DiffUnavailableException)
            {
                // Не читается — тоже не утечка.
            }
        }
    }

    [LinkFact]
    public async Task Junction_на_вложенный_репозиторий_ведёт_себя_как_вложенный_репозиторий()
    {
        using var repository = GitDiffTestRepository.Create();
        repository.Write("tracked.txt", "x\n");
        repository.CommitAll("base");
        var nested = Path.Combine(repository.Container, "вложенный");
        Directory.CreateDirectory(nested);
        GitDiffTestRepository.RunGit(nested, "init", "-q");
        File.WriteAllText(Path.Combine(nested, "n.txt"), Secret + "\n");
        Assert.True(LinkFactAttribute.TryCreateJunction(repository.Combine("jsub"), nested), "Не создался junction");

        var reader = CreateReader();
        var index = await reader.ListChangesAsync(new DiffRequest(repository.Root, null, [], false), CancellationToken.None);
        var entry = Assert.Single(index.Files, static f => f.Path.StartsWith("jsub", StringComparison.Ordinal));

        // git выдаёт такой junction как вложенный репозиторий — со слэшем в конце.
        Assert.Equal("jsub/", entry.Path);
        Assert.Equal((0, 0), (entry.AddedLines, entry.DeletedLines));
        var diff = await reader.ReadFileDiffAsync(index, entry, DiffContext.Hunks, CancellationToken.None);
        Assert.DoesNotContain(Secret, diff.Text, StringComparison.Ordinal);
    }

    [LinkFact]
    public void Проверка_по_handle_ловит_файл_открытый_через_ссылку_наружу()
    {
        using var repository = GitDiffTestRepository.Create();
        var (_, secretFile) = CreateOutside(repository);
        repository.Write("inside.txt", "x\n");
        var linked = LinkFactAttribute.TryCreateFileSymlink(repository.Combine("swap.txt"), secretFile);
        var resolver = new UntrackedPathResolver(repository.Root);

        using (var inside = File.OpenRead(repository.Combine("inside.txt")))
        {
            Assert.True(resolver.IsInsideRoot(inside.SafeFileHandle));
        }

        using (var direct = File.OpenRead(secretFile))
        {
            Assert.False(resolver.IsInsideRoot(direct.SafeFileHandle));
        }

        if (linked)
        {
            // Подмена после Resolve: открытие по пути внутри корня разыменовало ссылку наружу.
            using var swapped = File.OpenRead(repository.Combine("swap.txt"));
            Assert.False(resolver.IsInsideRoot(swapped.SafeFileHandle));
        }
    }

    [Fact]
    public async Task Удалённый_файл_даёт_причину_ОС_а_не_вне_репозитория()
    {
        using var repository = GitDiffTestRepository.Create();
        var resolver = new UntrackedPathResolver(repository.Root);

        var counts = await DiffUntrackedFile.CountLinesAsync(resolver, "нет/такого.txt", new GitDiffOptions(), new DiffReadBudget(1024), CancellationToken.None);
        var failure = await Assert.ThrowsAnyAsync<IOException>(
            () => DiffUntrackedFile.BuildDiffAsync(resolver, "нет.txt", new GitDiffOptions(), CancellationToken.None));

        Assert.Equal(new DiffLineCounts(null, 0), counts);
        Assert.IsType<FileNotFoundException>(failure);
        Assert.DoesNotContain("вне репозитория", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ссылк", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\?\D:\repo", @"\\?\D:\repo\a.txt", true)]
    [InlineData(@"\\?\D:\repo", @"\\?\d:\REPO\папка\a.txt", true)]
    [InlineData(@"\\?\D:\repo\", @"\\?\D:\repo\a.txt", true)]
    [InlineData(@"\\?\D:\", @"\\?\D:\a.txt", true)]
    [InlineData(@"\\?\D:\repo", @"\\?\D:\repo2\a.txt", false)]
    [InlineData(@"\\?\D:\repo", @"\\?\D:\repo", false)]
    [InlineData(@"\\?\D:\repo", @"\\?\D:\other\a.txt", false)]
    [InlineData(@"\\?\D:\repo", @"\\?\E:\repo\a.txt", false)]
    [InlineData(@"\\?\UNC\srv\share\repo", @"\\?\UNC\srv\share\repo\a.txt", true)]
    public void Итоговый_путь_сравнивается_без_регистра_по_границе_сегмента(string root, string path, bool expected)
    {
        Assert.Equal(expected, UntrackedPathResolver.IsWithin(root, path));
    }

    private static (string Outside, string SecretFile) CreateOutside(GitDiffTestRepository repository)
    {
        var outside = Path.Combine(repository.Container, "снаружи");
        Directory.CreateDirectory(outside);
        var secretFile = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secretFile, Secret + "\n");
        return (outside, secretFile);
    }

    private static GitDiffReader CreateReader()
    {
        var options = new GitDiffOptions();
        return new GitDiffReader(options, new GitProcessRunner(options, new GitProcessGate(options.MaxConcurrentProcesses)), new DiffCollapsePolicy(options));
    }
}

/// <summary>
/// Тест со ссылками: пропускается явно, если в этой системе нельзя создать ни symlink
/// (нужны права или Developer Mode), ни junction. На CI (<c>GITHUB_ACTIONS=true</c>) не
/// пропускается — там отсутствие ссылок само ошибка окружения.
/// </summary>
internal sealed class LinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public LinkFactAttribute()
    {
        var onCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        if (!onCi && !Available.Value)
        {
            Skip = "Нельзя создать ни symlink, ни junction: нет прав/Developer Mode или ФС без точек повторной обработки.";
        }
    }

    public static bool TryCreateFileSymlink(string link, string target) =>
        TryCreate(() => File.CreateSymbolicLink(link, target));

    public static bool TryCreateDirectorySymlink(string link, string target) =>
        TryCreate(() => Directory.CreateSymbolicLink(link, target));

    public static bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryCreate(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool Probe()
    {
        var probe = Path.Combine(Path.GetTempPath(), "cas-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var target = Path.Combine(probe, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "f.txt"), "x");
            return TryCreateFileSymlink(Path.Combine(probe, "ln"), Path.Combine(target, "f.txt"))
                || TryCreateJunction(Path.Combine(probe, "jn"), target);
        }
        finally
        {
            try
            {
                Directory.Delete(probe, recursive: true);
            }
            catch (IOException)
            {
                // Временный каталог пробы; остаток не мешает тестам.
            }
        }
    }
}
