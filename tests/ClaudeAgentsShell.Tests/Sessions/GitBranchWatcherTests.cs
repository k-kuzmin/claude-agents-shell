using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class GitBranchWatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly SessionsOptions Options = new()
    {
        GitBranchDebounce = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Смена_ветки_в_обычном_репозитории_доезжает_до_подписчика()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "repo", "ref: refs/heads/main\n");
        var headFile = Path.Combine(repository, ".git", "HEAD");

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var changed = NextBranch(watcher);

        await watcher.WatchAsync(repository, CancellationToken.None);
        await WriteHeadAsync(headFile, "ref: refs/heads/stage/m2-tabs\n");

        var args = await changed.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal("stage/m2-tabs", args.Branch);
        Assert.Equal(repository, args.WorkingDirectory);
    }

    [Fact]
    public async Task В_worktree_слежение_идёт_за_настоящим_каталогом_git()
    {
        using var temp = new TempDirectory();
        var main = CreateRepository(temp, "main", "ref: refs/heads/main\n");
        var linked = temp.Combine("worktrees", "wo-14693");
        Directory.CreateDirectory(linked);

        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wo-14693");
        Directory.CreateDirectory(worktreeGitDir);
        var headFile = Path.Combine(worktreeGitDir, "HEAD");
        await File.WriteAllTextAsync(headFile, "ref: refs/heads/WO-14693\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(linked, ".git"), "gitdir: " + worktreeGitDir + "\n", CancellationToken.None);

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var changed = NextBranch(watcher);

        await watcher.WatchAsync(linked, CancellationToken.None);
        await WriteHeadAsync(headFile, "ref: refs/heads/WO-14693-fix\n");

        var args = await changed.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal("WO-14693-fix", args.Branch);
    }

    [Fact]
    public async Task Отделение_головы_сообщается_как_отсутствие_ветки()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "detach", "ref: refs/heads/main\n");
        var headFile = Path.Combine(repository, ".git", "HEAD");

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var changed = NextBranch(watcher);

        await watcher.WatchAsync(repository, CancellationToken.None);
        await WriteHeadAsync(headFile, "75d8cb2f1b0a1c2d3e4f5a6b7c8d9e0f11223344\n");

        var args = await changed.WaitAsync(Timeout, CancellationToken.None);

        Assert.Null(args.Branch);
    }

    [Fact]
    public async Task Не_репозиторий_и_исчезнувший_каталог_не_ошибка()
    {
        using var temp = new TempDirectory();
        var plain = temp.Combine("plain");
        Directory.CreateDirectory(plain);

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);

        await watcher.WatchAsync(plain, CancellationToken.None);
        await watcher.WatchAsync(temp.Combine("нет-такого"), CancellationToken.None);
        watcher.Unwatch(plain);
    }

    [Fact]
    public async Task Повторное_слежение_за_тем_же_каталогом_безвредно()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "repo", "ref: refs/heads/main\n");
        var headFile = Path.Combine(repository, ".git", "HEAD");

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var events = new List<GitBranchChangedEventArgs>();
        var first = new TaskCompletionSource<GitBranchChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BranchChanged += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
            }

            first.TrySetResult(args);
        };

        await watcher.WatchAsync(repository, CancellationToken.None);
        await watcher.WatchAsync(repository + Path.DirectorySeparatorChar, CancellationToken.None);

        await WriteHeadAsync(headFile, "ref: refs/heads/second\n");
        await first.Task.WaitAsync(Timeout, CancellationToken.None);

        // Дубль подписки дал бы второе уведомление о том же переходе.
        await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);

        lock (events)
        {
            Assert.Equal(["second"], events.Select(static e => e.Branch));
        }
    }

    [Fact]
    public async Task После_отмены_слежения_события_не_приходят()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "repo", "ref: refs/heads/main\n");
        var headFile = Path.Combine(repository, ".git", "HEAD");

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var raised = 0;
        watcher.BranchChanged += (_, _) => Interlocked.Increment(ref raised);

        await watcher.WatchAsync(repository, CancellationToken.None);
        watcher.Unwatch(repository);

        await WriteHeadAsync(headFile, "ref: refs/heads/third\n");
        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Исчезновение_каталога_git_снимает_слежение_и_даёт_поднять_его_заново()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp, "repo", "ref: refs/heads/main\n");
        var gitDirectory = Path.Combine(repository, ".git");

        await using var watcher = new GitBranchWatcher(new GitBranchReader(), Options, TimeProvider.System);
        var branches = new List<string?>();
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BranchChanged += (_, args) =>
        {
            lock (branches)
            {
                branches.Add(args.Branch);
            }

            if (args.Branch is null)
            {
                gone.TrySetResult();
            }
            else if (args.Branch == "восстановлена-2")
            {
                restored.TrySetResult();
            }
        };

        await watcher.WatchAsync(repository, CancellationToken.None);

        // Каталог git исчез — наблюдателю больше неоткуда брать события.
        Directory.Delete(gitDirectory, recursive: true);
        await gone.Task.WaitAsync(Timeout, CancellationToken.None);

        // Репозиторий вернулся: повторный WatchAsync обязан поднять слежение, а не упереться
        // в мёртвую запись словаря.
        Directory.CreateDirectory(gitDirectory);
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/восстановлена\n", CancellationToken.None);
        await watcher.WatchAsync(repository, CancellationToken.None);
        await WriteHeadAsync(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/восстановлена-2\n");

        await restored.Task.WaitAsync(Timeout, CancellationToken.None);
    }

    private static Task<GitBranchChangedEventArgs> NextBranch(IGitBranchWatcher watcher)
    {
        var completion = new TaskCompletionSource<GitBranchChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BranchChanged += (_, args) => completion.TrySetResult(args);
        return completion.Task;
    }

    /// <summary>Пишет HEAD так же, как git: временный файл и переименование поверх.</summary>
    private static async Task WriteHeadAsync(string headFile, string content)
    {
        var lockFile = headFile + ".lock";
        await File.WriteAllTextAsync(lockFile, content, CancellationToken.None);
        File.Move(lockFile, headFile, overwrite: true);
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
