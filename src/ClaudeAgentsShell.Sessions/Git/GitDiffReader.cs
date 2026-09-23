using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Diff ветки против базы из git (issue #5). Оглавление — пять-шесть запусков git независимо от
/// числа файлов: корень, база, <c>merge-base</c>, <c>diff --raw --numstat</c> (с <c>-w</c> — ещё
/// <c>diff --numstat -w</c> для счётчиков), <c>ls-files --others</c>,
/// <c>check-attr</c>. Строки неотслеживаемых файлов считаются в процессе. Содержимое файла — один
/// запуск с потолком вывода. Индекс и рабочее дерево не меняются.
/// </summary>
public sealed class GitDiffReader : IGitDiffReader
{
    private const string HunksContext = "-U3";
    private const string FullFileContext = "-U999999";

    private static readonly string[] DiffFlags =
    [
        "--no-color",
        "--no-ext-diff",
        "--no-textconv",
        "-M",
    ];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GitDiffOptions _options;
    private readonly GitProcessRunner _runner;
    private readonly DiffCollapsePolicy _policy;

    /// <inheritdoc cref="GitDiffReader" />
    public GitDiffReader(GitDiffOptions options, GitProcessRunner runner, DiffCollapsePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(policy);
        _options = options;
        _runner = runner;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<DiffIndex> ListChangesAsync(DiffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = StartTimeout(cancellationToken);
        try
        {
            // Task.Run: Directory.Exists и Process.Start синхронны и не должны идти в потоке вызывающего (UI).
            var token = timeout.Token;
            return await Task.Run(() => ListCoreAsync(request, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutFailure();
        }
    }

    /// <inheritdoc />
    public async Task<FileDiff> ReadFileDiffAsync(DiffIndex index, DiffFileEntry file, DiffContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(file);
        using var timeout = StartTimeout(cancellationToken);
        try
        {
            var token = timeout.Token;
            return await Task.Run(
                () => file.Kind == DiffChangeKind.Untracked
                    ? ReadUntrackedAsync(index, file, context, token)
                    : ReadTrackedAsync(index, file, context, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutFailure();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        using var timeout = StartTimeout(cancellationToken);
        try
        {
            var token = timeout.Token;
            return await Task.Run(() => ListWorktreesCoreAsync(directory, token), token).ConfigureAwait(false);
        }
        catch (DiffUnavailableException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<GitWorktree>> ListWorktreesCoreAsync(string directory, CancellationToken cancellationToken)
    {
        var root = await ResolveRootAsync(directory, cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(root, ["worktree", "list", "--porcelain", "-z"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // git старше 2.36 не знает -z у worktree list.
            result = await RunAsync(root, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        }

        return result.Succeeded ? GitDiffOutputParser.ParseWorktreeList(Decode(result.Output), root) : [];
    }

    private async Task<DiffIndex> ListCoreAsync(DiffRequest request, CancellationToken cancellationToken)
    {
        var root = await ResolveRootAsync(request.Directory, cancellationToken).ConfigureAwait(false);
        var selection = DiffPaths.NormalizeRequested(request.Files, root);
        if (selection.AllOutside)
        {
            throw new DiffUnavailableException(DiffFailure.GitFailed, "Указанные файлы лежат вне репозитория " + root + ".");
        }

        var requested = selection.Paths;

        // Неотслеживаемые не зависят от базы — считаются параллельно с её поиском.
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var untrackedTask = ListUntrackedAsync(root, requested, scope.Token);
        (string Name, string MergeBase) @base;
        IReadOnlyList<DiffFileEntry> tracked;
        IReadOnlyList<DiffFileEntry> untracked;
        try
        {
            @base = await ResolveBaseAsync(root, request.BaseRef, cancellationToken).ConfigureAwait(false);
            tracked = await ListTrackedAsync(root, @base.MergeBase, request.IgnoreWhitespace, requested, cancellationToken).ConfigureAwait(false);
            untracked = await untrackedTask.ConfigureAwait(false);
        }
        catch
        {
            await scope.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(untrackedTask).ConfigureAwait(false);
            throw;
        }

        var seen = new HashSet<string>(tracked.Select(static entry => entry.Path), StringComparer.Ordinal);
        var all = tracked.Concat(untracked.Where(entry => seen.Add(entry.Path))).ToList();
        var attributes = await ReadAttributesAsync(root, all, cancellationToken).ConfigureAwait(false);

        // Не сворачиваются только файлы, названные точно; попавшие под названный каталог
        // сворачиваются как обычно и раскрываются страницей в пределах бюджета.
        var named = new HashSet<string>(requested, StringComparer.Ordinal);
        var files = new List<DiffFileEntry>(all.Count);
        foreach (var entry in all)
        {
            attributes.TryGetValue(entry.Path, out var pathAttributes);
            var collapse = _policy.Classify(entry, pathAttributes, named.Contains(entry.Path));
            files.Add(entry with { Collapse = collapse });
        }

        return new DiffIndex(root, @base.Name, @base.MergeBase, request.IgnoreWhitespace, files);
    }

    private async Task<string> ResolveRootAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DiffUnavailableException(DiffFailure.NotARepository, "Каталог не найден: " + directory);
        }

        var result = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase))
            {
                throw new DiffUnavailableException(DiffFailure.NotARepository, "Каталог не в git-репозитории: " + directory);
            }

            throw Failed("git rev-parse", result);
        }

        var root = Decode(result.Output).Trim();
        if (root.Length == 0)
        {
            throw new DiffUnavailableException(DiffFailure.NotARepository, "У каталога нет рабочего дерева git: " + directory);
        }

        return GitDiffOutputParser.NormalizeDirectory(root);
    }

    private async Task<(string Name, string MergeBase)> ResolveBaseAsync(string root, string? baseRef, CancellationToken cancellationToken)
    {
        GitBaseCandidate candidate;
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            var name = baseRef.Trim();
            if (name.StartsWith('-'))
            {
                throw new DiffUnavailableException(DiffFailure.NoBase, "Недопустимое имя базы: " + name);
            }

            candidate = new GitBaseCandidate(name, name);
        }
        else
        {
            var refs = await RunAsync(
                root,
                ["for-each-ref", "--format=%(refname)%00%(symref)", .. GitDiffOutputParser.DefaultBaseRefs],
                cancellationToken).ConfigureAwait(false);
            if (!refs.Succeeded)
            {
                throw Failed("git for-each-ref", refs);
            }

            candidate = GitDiffOutputParser.PickDefaultBase(Decode(refs.Output))
                ?? throw new DiffUnavailableException(DiffFailure.NoBase, "Базовая ветка не найдена: нет ни origin/HEAD, ни main, ни master. Укажите базу явно.");
        }

        var mergeBase = await RunAsync(root, ["merge-base", "HEAD", candidate.Revision], cancellationToken).ConfigureAwait(false);
        var sha = Decode(mergeBase.Output).Trim();
        if (!mergeBase.Succeeded || sha.Length == 0)
        {
            throw new DiffUnavailableException(DiffFailure.NoBase, "Нет общего предка HEAD и " + candidate.Name + ".");
        }

        return (candidate.Name, sha);
    }

    private async Task<IReadOnlyList<DiffFileEntry>> ListTrackedAsync(
        string root, string mergeBase, bool ignoreWhitespace, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        // Список файлов — всегда без -w: git 2.55 под -w не выдаёт raw-запись файла, где изменены
        // только пробелы, а файл должен остаться в оглавлении. С -w берутся только счётчики строк,
        // второй командой параллельно — тем же пулом процессов и той же отменой.
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var countsTask = ignoreWhitespace
            ? ListWhitespaceIgnoredCountsAsync(root, mergeBase, requested, scope.Token)
            : null;

        IReadOnlyList<DiffFileEntry> entries;
        try
        {
            var result = await RunAsync(root, DiffArguments(["--raw", "--numstat"], ignoreWhitespace: false, mergeBase, requested), cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw Failed("git diff", result);
            }

            entries = GitDiffOutputParser.ParseRawNumstat(Decode(result.Output));
        }
        catch when (countsTask is not null)
        {
            await scope.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(countsTask).ConfigureAwait(false);
            throw;
        }

        return countsTask is null
            ? entries
            : GitDiffOutputParser.WithWhitespaceIgnoredCounts(entries, await countsTask.ConfigureAwait(false));
    }

    private async Task<IReadOnlyDictionary<string, (int? Added, int? Deleted)>> ListWhitespaceIgnoredCountsAsync(
        string root, string mergeBase, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, DiffArguments(["--numstat"], ignoreWhitespace: true, mergeBase, requested), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed("git diff -w", result);
        }

        return GitDiffOutputParser.ParseNumstat(Decode(result.Output));
    }

    private static List<string> DiffArguments(
        IReadOnlyList<string> formats, bool ignoreWhitespace, string mergeBase, IReadOnlyList<string> requested)
    {
        var arguments = new List<string> { "diff" };
        arguments.AddRange(formats);
        arguments.Add("-z");
        arguments.AddRange(DiffFlags);
        if (ignoreWhitespace)
        {
            arguments.Add("-w");
        }

        arguments.Add(mergeBase);
        arguments.Add("--");
        arguments.AddRange(requested);
        return arguments;
    }

    private async Task<IReadOnlyList<DiffFileEntry>> ListUntrackedAsync(string root, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["ls-files", "--others", "--exclude-standard", "-z", "--", .. requested], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed("git ls-files", result);
        }

        var paths = GitDiffOutputParser.SplitNul(Decode(result.Output));
        // Чтение асинхронное (IOCP), несколько файлов сразу; общий бюджет держит оглавление
        // быстрым, даже если агент насоздавал сотни крупных файлов.
        var entries = new DiffFileEntry[paths.Count];
        var budget = new DiffReadBudget(_options.UntrackedCountBudgetBytes);
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.UntrackedCountParallelism),
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(
            Enumerable.Range(0, paths.Count),
            parallel,
            async (i, token) =>
            {
                var counts = await DiffUntrackedFile.CountLinesAsync(Path.Combine(root, paths[i]), _options, budget, token).ConfigureAwait(false);
                entries[i] = new DiffFileEntry(paths[i], null, DiffChangeKind.Untracked, counts.Added, counts.Deleted, DiffCollapseReason.None);
            }).ConfigureAwait(false);

        return entries;
    }

    private async Task<IReadOnlyDictionary<string, GitPathAttributes>> ReadAttributesAsync(
        string root, IReadOnlyList<DiffFileEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return new Dictionary<string, GitPathAttributes>();
        }

        var input = new StringBuilder();
        foreach (var entry in entries)
        {
            input.Append(entry.Path).Append('\0');
        }

        var result = await _runner.RunAsync(
            root,
            ["check-attr", "-z", "--stdin", "linguist-generated", "diff"],
            Utf8.GetBytes(input.ToString()),
            outputCeilingBytes: null,
            cancellationToken).ConfigureAwait(false);

        // Атрибуты — уточнение, а не условие: без них работает встроенный список.
        return result.Succeeded
            ? GitDiffOutputParser.ParseCheckAttr(Decode(result.Output))
            : new Dictionary<string, GitPathAttributes>();
    }

    private async Task<FileDiff> ReadTrackedAsync(DiffIndex index, DiffFileEntry file, DiffContext context, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "diff" };
        arguments.AddRange(DiffFlags);
        arguments.Add("--src-prefix=a/");
        arguments.Add("--dst-prefix=b/");
        arguments.Add(context == DiffContext.FullFile ? FullFileContext : HunksContext);
        if (index.IgnoreWhitespace)
        {
            arguments.Add("-w");
        }

        arguments.Add(index.MergeBase);
        arguments.Add("--");
        if (file.OldPath is not null)
        {
            arguments.Add(file.OldPath);
        }

        arguments.Add(file.Path);

        var result = await _runner.RunAsync(
            index.RepositoryRoot, arguments, null, _options.FileOutputCeilingBytes, cancellationToken).ConfigureAwait(false);
        if (result.Truncated)
        {
            var output = result.Output;
            var length = Array.LastIndexOf(output, (byte)'\n') + 1;
            return new FileDiff(file.Path, context, Utf8Decode(output, length), Truncated: true);
        }

        if (!result.Succeeded)
        {
            throw Failed("git diff", result);
        }

        return new FileDiff(file.Path, context, Decode(result.Output), Truncated: false);
    }

    private async Task<FileDiff> ReadUntrackedAsync(DiffIndex index, DiffFileEntry file, DiffContext context, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(index.RepositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
        {
            throw new DiffUnavailableException(DiffFailure.GitFailed, "Файл вне репозитория: " + file.Path);
        }

        try
        {
            var diff = await DiffUntrackedFile.BuildDiffAsync(fullPath, file.Path, _options, cancellationToken).ConfigureAwait(false);
            return new FileDiff(file.Path, context, diff.Text, diff.Truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DiffUnavailableException(DiffFailure.GitFailed, "Не удалось прочитать " + file.Path + ": " + exception.Message, exception);
        }
    }

    private Task<GitProcessResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _runner.RunAsync(workingDirectory, arguments, standardInput: null, outputCeilingBytes: null, cancellationToken);

    private CancellationTokenSource StartTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.Timeout);
        return source;
    }

    private DiffUnavailableException TimeoutFailure() =>
        new(DiffFailure.Timeout, $"git не уложился в {_options.Timeout.TotalSeconds:0} с.");

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Сбой параллельной ветки уже не важен: наружу уходит первая причина.
        }
    }

    private static DiffUnavailableException Failed(string command, GitProcessResult result) =>
        new(DiffFailure.GitFailed, command + " завершился с кодом " + result.ExitCode + (result.Error.Length > 0 ? ": " + result.Error : "."));

    private static string Decode(byte[] bytes) => Utf8Decode(bytes, bytes.Length);

    // Кодировка файла может быть не UTF-8 (cp1251): битые байты заменяются, а не роняют разбор.
    private static string Utf8Decode(byte[] bytes, int length) => Utf8.GetString(bytes, 0, length);
}
