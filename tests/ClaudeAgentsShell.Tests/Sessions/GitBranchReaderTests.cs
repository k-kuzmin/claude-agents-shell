using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitBranchReaderTests
{
    private readonly GitBranchReader _reader = new();

    [Fact]
    public async Task Обычный_репозиторий_отдаёт_имя_ветки()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "repo", "ref: refs/heads/stage/m2-tabs\n");

        Assert.Equal("stage/m2-tabs", await _reader.ReadAsync(repository, CancellationToken.None));
    }

    [Fact]
    public async Task Worktree_с_git_файлом_читается_по_ссылке_gitdir()
    {
        using var temp = new TempDirectory();
        var main = CreateRepository(temp, "main", "ref: refs/heads/main\n");
        var linked = temp.Combine("worktrees", "wo-10365");
        Directory.CreateDirectory(linked);

        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wo-10365");
        Directory.CreateDirectory(worktreeGitDir);
        await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/WO-10365\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(linked, ".git"), "gitdir: " + worktreeGitDir + "\n", CancellationToken.None);

        Assert.Equal("WO-10365", await _reader.ReadAsync(linked, CancellationToken.None));
    }

    [Fact]
    public async Task Относительный_gitdir_разрешается_от_каталога_проекта()
    {
        using var temp = new TempDirectory();
        var submodule = temp.Combine("submodule");
        Directory.CreateDirectory(submodule);
        var realGitDir = temp.Combine("modules", "sub");
        Directory.CreateDirectory(realGitDir);
        await File.WriteAllTextAsync(Path.Combine(realGitDir, "HEAD"), "ref: refs/heads/feature/x\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(submodule, ".git"), "gitdir: ../modules/sub\n", CancellationToken.None);

        Assert.Equal("feature/x", await _reader.ReadAsync(submodule, CancellationToken.None));
    }

    [Fact]
    public async Task Отделённая_голова_ветки_не_даёт()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "detached", "75d8cb2f1b0a1c2d3e4f5a6b7c8d9e0f11223344\n");

        Assert.Null(await _reader.ReadAsync(repository, CancellationToken.None));
    }

    [Fact]
    public async Task Не_репозиторий_ветки_не_даёт()
    {
        using var temp = new TempDirectory();
        var plain = temp.Combine("plain");
        Directory.CreateDirectory(plain);

        Assert.Null(await _reader.ReadAsync(plain, CancellationToken.None));
    }

    [Fact]
    public async Task Исчезнувший_каталог_ветки_не_даёт_и_не_бросает()
    {
        using var temp = new TempDirectory();

        Assert.Null(await _reader.ReadAsync(temp.Combine("нет-такого"), CancellationToken.None));
        Assert.Null(await _reader.ReadAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Битый_git_файл_ветки_не_даёт()
    {
        using var temp = new TempDirectory();
        var broken = temp.Combine("broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, ".git"), "мусор без gitdir\n", CancellationToken.None);

        Assert.Null(await _reader.ReadAsync(broken, CancellationToken.None));
    }

    [Fact]
    public async Task Ссылка_вне_refs_heads_веткой_не_считается()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "remote-head", "ref: refs/remotes/origin/main\n");

        Assert.Null(await _reader.ReadAsync(repository, CancellationToken.None));
    }

    private static string CreateRepository(TempDirectory temp, string name, string head)
    {
        var root = temp.Combine(name);
        var gitDirectory = Path.Combine(root, ".git");
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), head);
        return root;
    }
}
