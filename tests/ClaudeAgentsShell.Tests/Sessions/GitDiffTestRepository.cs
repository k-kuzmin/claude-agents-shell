using System.Diagnostics;
using System.Text;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Временный git-репозиторий для тестов чтения diff. Каталог с кириллицей и пробелом в имени —
/// чтобы пути проверялись заодно. Удаляется в <see cref="Dispose"/> вместе с read-only объектами git.
/// </summary>
internal sealed class GitDiffTestRepository : IDisposable
{
    private GitDiffTestRepository(string container, string root)
    {
        Container = container;
        Root = root;
    }

    /// <summary>Каталог, в котором лежит репозиторий, — для соседних worktree.</summary>
    public string Container { get; }

    public string Root { get; }

    public static GitDiffTestRepository Create(string initialBranch = "main")
    {
        var container = Path.Combine(Path.GetTempPath(), "cas-git-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(container, "репо с пробелом");
        Directory.CreateDirectory(root);
        var repository = new GitDiffTestRepository(container, root);
        repository.Git("init", "-q", "-b", initialBranch);
        repository.Git("config", "core.autocrlf", "false");
        repository.Git("config", "commit.gpgsign", "false");
        return repository;
    }

    public string Combine(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content) => WriteBytes(relative, Encoding.UTF8.GetBytes(content));

    public void WriteBytes(string relative, byte[] content)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void CommitAll(string message = "commit")
    {
        Git("add", "-A");
        Git("commit", "-q", "--no-verify", "-m", message);
    }

    public string Git(params string[] arguments) => RunGit(Root, arguments);

    public static string RunGit(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "core.quotepath=false", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git " + string.Join(' ', arguments) + ": " + errorTask.Result);
        }

        return output;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Container))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Container, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Container, recursive: true);
        }
        catch (IOException)
        {
            // Антивирус или ещё открытый дескриптор — уборка не должна валить зелёный тест.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
