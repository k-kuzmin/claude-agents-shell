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

        var outside = Path.Combine(repository.Container, "снаружи");
        Directory.CreateDirectory(outside);
        var secretFile = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secretFile, Secret + "\n");

        var symlink = LinkFactAttribute.TryCreateFileSymlink(repository.Combine("ln.txt"), secretFile);
        var junction = LinkFactAttribute.TryCreateJunction(repository.Combine("jn"), outside);
        Assert.True(symlink || junction, "LinkFact пропустил бы тест без ссылок");

        var options = new GitDiffOptions();
        var reader = new GitDiffReader(options, new GitProcessRunner(options, new GitProcessGate(options.MaxConcurrentProcesses)), new DiffCollapsePolicy(options));
        var index = await reader.ListChangesAsync(new DiffRequest(repository.Root, null, [], false), CancellationToken.None);
        var files = index.Files.ToDictionary(static f => f.Path);

        if (symlink)
        {
            var link = files["ln.txt"];
            Assert.Equal((1, 0), (link.AddedLines, link.DeletedLines));
            var diff = await reader.ReadFileDiffAsync(index, link, DiffContext.Hunks, CancellationToken.None);
            Assert.Contains("new file mode 120000", diff.Text, StringComparison.Ordinal);
            Assert.Contains("+" + secretFile, diff.Text, StringComparison.Ordinal);
            Assert.Contains("\\ No newline at end of file", diff.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, diff.Text, StringComparison.Ordinal);
        }

        if (junction)
        {
            // git перечисляет файлы за junction как свои; читать их нельзя.
            var behind = index.Files.Where(static f => f.Path == "jn" || f.Path.StartsWith("jn/", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(behind);
            foreach (var entry in behind)
            {
                try
                {
                    var diff = await reader.ReadFileDiffAsync(index, entry, DiffContext.Hunks, CancellationToken.None);
                    Assert.DoesNotContain(Secret, diff.Text, StringComparison.Ordinal);
                }
                catch (DiffUnavailableException)
                {
                    // Путь через ссылку-каталог не читается — ожидаемо.
                }

                if (entry.Path != "jn")
                {
                    Assert.Null(entry.AddedLines);
                }
            }
        }
    }
}

/// <summary>
/// Тест со ссылками: пропускается явно, если в этой системе нельзя создать ни symlink
/// (нужны права или Developer Mode), ни junction.
/// </summary>
internal sealed class LinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public LinkFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "Нельзя создать ни symlink, ни junction: нет прав/Developer Mode или ФС без точек повторной обработки.";
        }
    }

    public static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

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
            }
        }
    }
}
