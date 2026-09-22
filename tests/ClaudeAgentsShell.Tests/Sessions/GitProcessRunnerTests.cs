using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Тесты на «живых git не осталось» смотрят на потомков процесса тестов, поэтому идут без
/// параллельных соседей: чужой git из другого теста выглядел бы как утечка.
/// </summary>
[CollectionDefinition(nameof(GitProcessTreeCollection), DisableParallelization = true)]
public sealed class GitProcessTreeCollection
{
}

[Collection(nameof(GitProcessTreeCollection))]
public sealed class GitProcessRunnerTests
{
    [Fact]
    public async Task Отмена_снимает_дерево_процессов()
    {
        using var temp = new TempDirectory();
        var runner = new GitProcessRunner(new GitDiffOptions());
        using var cancellation = new CancellationTokenSource();

        // alias с ! запускает sh, тот — sleep: у git есть внуки.
        var run = runner.RunAsync(temp.Path, ["-c", "alias.slow=!sleep 60", "slow"], null, null, cancellation.Token);
        var tree = await WaitForDescendantsAsync(static names => names.Contains("sleep"));
        var stopwatch = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.All(tree, static pid => Assert.False(IsAlive(pid), "процесс " + pid + " жив"));
    }

    [Fact]
    public async Task Таймаут_читателя_даёт_Timeout_и_не_оставляет_процесса()
    {
        using var repository = GitDiffTestRepository.Create();
        repository.Write("a.txt", "a\n");
        repository.CommitAll();
        var options = new GitDiffOptions { Timeout = TimeSpan.FromMilliseconds(1) };
        var reader = new GitDiffReader(options, new GitProcessRunner(options), new DiffCollapsePolicy(options));

        var failure = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => reader.ListChangesAsync(new DiffRequest(repository.Root, null, [], false), CancellationToken.None));

        Assert.Equal(DiffFailure.Timeout, failure.Failure);
        Assert.DoesNotContain(ProcessTree.Descendants(Environment.ProcessId), static pid => GitFamily.Contains(Path.GetFileNameWithoutExtension(ProcessTree.NameOf(pid))));
    }

    [Fact]
    public async Task Потолок_снимает_процесс_и_помечает_вывод()
    {
        using var repository = GitDiffTestRepository.Create();
        repository.Write("a.txt", string.Concat(Enumerable.Repeat("строка\n", 100_000)));
        repository.CommitAll();
        var runner = new GitProcessRunner(new GitDiffOptions());

        var result = await runner.RunAsync(repository.Root, ["show", "HEAD:a.txt"], null, 1000, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Null(result.ExitCode);
        Assert.Equal(1000, result.Output.Length);
        Assert.DoesNotContain(ProcessTree.Descendants(Environment.ProcessId), static pid => GitFamily.Contains(Path.GetFileNameWithoutExtension(ProcessTree.NameOf(pid))));
    }

    [Fact]
    public async Task Stdin_подаётся_и_закрывается()
    {
        using var repository = GitDiffTestRepository.Create();
        var runner = new GitProcessRunner(new GitDiffOptions());

        var result = await runner.RunAsync(
            repository.Root, ["check-attr", "-z", "--stdin", "diff"], Encoding.UTF8.GetBytes("путь с пробелом.txt\0"), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("путь с пробелом.txt\0diff\0unspecified\0", Encoding.UTF8.GetString(result.Output));
    }

    [Fact]
    public async Task Нет_исполняемого_файла_GitNotFound()
    {
        using var temp = new TempDirectory();
        var runner = new GitProcessRunner(new GitDiffOptions { GitExecutable = "cas-no-such-git-" + Guid.NewGuid().ToString("N") });

        var failure = await Assert.ThrowsAsync<DiffUnavailableException>(
            () => runner.RunAsync(temp.Path, ["--version"], null, null, CancellationToken.None));

        Assert.Equal(DiffFailure.GitNotFound, failure.Failure);
    }

    private static readonly HashSet<string> GitFamily = new(["git", "sh", "bash", "sleep"], StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<int>> WaitForDescendantsAsync(Func<IReadOnlySet<string>, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            // conhost не в счёт: он закрывается сам вслед за клиентом и может чуть задержаться.
            var tree = ProcessTree.Descendants(Environment.ProcessId)
                .Where(static pid => GitFamily.Contains(Path.GetFileNameWithoutExtension(ProcessTree.NameOf(pid))))
                .ToList();
            var names = tree.Select(static pid => Path.GetFileNameWithoutExtension(ProcessTree.NameOf(pid))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ready(names))
            {
                return tree;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("git не запустил дочерние процессы");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Снимок дерева процессов через Toolhelp32: у <see cref="Process"/> нет родителя.</summary>
    private static class ProcessTree
    {
        private const uint SnapProcess = 0x2;

        private static readonly Dictionary<int, string> Names = [];

        public static string NameOf(int pid) => Names.TryGetValue(pid, out var name) ? name : string.Empty;

        public static IReadOnlyList<int> Descendants(int root)
        {
            var children = new Dictionary<int, List<int>>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                {
                    var pid = (int)entry.ProcessId;
                    Names[pid] = entry.ExeFile;
                    if (!children.TryGetValue((int)entry.ParentProcessId, out var list))
                    {
                        children[(int)entry.ParentProcessId] = list = [];
                    }

                    list.Add(pid);
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var result = new List<int>();
            var queue = new Queue<int>([root]);
            while (queue.Count > 0)
            {
                if (!children.TryGetValue(queue.Dequeue(), out var list))
                {
                    continue;
                }

                foreach (var child in list.Where(child => child != root && !result.Contains(child)))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
