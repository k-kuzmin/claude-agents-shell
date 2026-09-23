using ClaudeAgentsShell.App.Diff;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Набор вкладок для координатора diff: вкладки и их токены задаёт тест.</summary>
internal sealed class FakeDiffTabs : IDiffTabs
{
    private readonly List<(TabViewModel Tab, string Token)> _tabs = [];

    public event EventHandler<DiffTabClosedEventArgs>? TabClosed;

    public TabViewModel Add(string id, string workingDirectory, bool active = false)
    {
        var tab = new TabViewModel(new TerminalId(id), Guid.NewGuid(), "alpha", workingDirectory) { IsActive = active };
        _tabs.Add((tab, TokenFor(id)));
        return tab;
    }

    public static string TokenFor(string id) => "tok-" + id;

    public void Close(TabViewModel tab)
    {
        _tabs.RemoveAll(entry => ReferenceEquals(entry.Tab, tab));
        TabClosed?.Invoke(this, new DiffTabClosedEventArgs(tab.TerminalId));
    }

    public bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId)
    {
        foreach (var (tab, token) in _tabs)
        {
            if (token == correlationToken)
            {
                terminalId = tab.TerminalId;
                return true;
            }
        }

        terminalId = default;
        return false;
    }

    public TabViewModel? Find(TerminalId terminalId)
    {
        return _tabs.Select(entry => entry.Tab).FirstOrDefault(tab => tab.TerminalId == terminalId);
    }
}

/// <summary>Git без процессов: ответы и задержки задаёт тест.</summary>
internal sealed class FakeGitDiffReader : IGitDiffReader
{
    private readonly object _sync = new();
    private int _inFlight;

    /// <summary>Ответ на запрос оглавления; по умолчанию — оглавление из <see cref="DefaultFiles"/>.</summary>
    public Func<DiffRequest, CancellationToken, Task<DiffIndex>>? ListChanges { get; set; }

    /// <summary>Ответ на запрос файла; по умолчанию — готовый diff сразу.</summary>
    public Func<DiffFileEntry, DiffContext, CancellationToken, Task<FileDiff>>? ReadFile { get; set; }

    public IReadOnlyList<GitWorktree> Worktrees { get; set; } = [new GitWorktree(@"D:\src\alpha", "main", true)];

    public IReadOnlyList<DiffFileEntry> DefaultFiles { get; set; } =
    [
        Entry("src/a.cs"),
        Entry("src/b.cs"),
    ];

    public List<DiffRequest> Requests { get; } = [];

    public List<string> WorktreeDirectories { get; } = [];

    public List<(string Path, DiffContext Context)> FileReads { get; } = [];

    /// <summary>Наибольшее число одновременных чтений файлов.</summary>
    public int MaxInFlight { get; private set; }

    public static DiffFileEntry Entry(string path) =>
        new(path, null, DiffChangeKind.Modified, 1, 1, DiffCollapseReason.None);

    public static DiffIndex IndexFor(DiffRequest request, IReadOnlyList<DiffFileEntry> files, string baseRef = "origin/main") =>
        new(request.Directory, request.BaseRef ?? baseRef, "abc123", request.IgnoreWhitespace, files);

    public Task<DiffIndex> ListChangesAsync(DiffRequest request, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            Requests.Add(request);
        }

        return ListChanges is { } answer
            ? answer(request, cancellationToken)
            : Task.FromResult(IndexFor(request, DefaultFiles));
    }

    public async Task<FileDiff> ReadFileDiffAsync(
        DiffIndex index,
        DiffFileEntry file,
        DiffContext context,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            FileReads.Add((file.Path, context));
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
        }

        try
        {
            return ReadFile is { } answer
                ? await answer(file, context, cancellationToken)
                : new FileDiff(file.Path, context, "@@ -1 +1 @@\n-a\n+b\n", Truncated: false);
        }
        finally
        {
            lock (_sync)
            {
                _inFlight--;
            }
        }
    }

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            WorktreeDirectories.Add(directory);
        }

        return Task.FromResult(Worktrees);
    }
}

/// <summary>Вызов панели diff, записанный фейком.</summary>
internal sealed record DiffViewCall(
    string Kind,
    TerminalId TerminalId,
    DiffIndex? Index = null,
    IReadOnlyList<GitWorktree>? Worktrees = null,
    string? Note = null,
    IReadOnlyList<string>? ExpandFiles = null,
    FileDiff? File = null,
    string? Path = null,
    string? Message = null);

/// <summary>Панель diff без страницы: записывает вызовы, события поднимает тест.</summary>
internal sealed class FakeDiffView : IDiffView
{
    private readonly object _sync = new();
    private readonly List<DiffViewCall> _calls = [];

    public event EventHandler<DiffRefreshRequestedEventArgs>? RefreshRequested;

    public event EventHandler<DiffFileRequestedEventArgs>? FileRequested;

    public event EventHandler<DiffClosedEventArgs>? Closed;

    public IReadOnlyList<DiffViewCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<DiffViewCall> CallsOf(string kind) => [.. Calls.Where(call => call.Kind == kind)];

    public bool HasSubscribers => RefreshRequested is not null || FileRequested is not null || Closed is not null;

    /// <summary>Вмешательство в вызов: вернуть незавершённую задачу, чтобы вызов «висел».</summary>
    public Func<DiffViewCall, ValueTask?>? OnCall { get; set; }

    public void RaiseRefresh(TerminalId id, string? directory, string? baseRef, bool ignoreWhitespace) =>
        RefreshRequested?.Invoke(this, new DiffRefreshRequestedEventArgs(id, directory, baseRef, ignoreWhitespace));

    public void RaiseFile(TerminalId id, string path, DiffContext context = DiffContext.Hunks) =>
        FileRequested?.Invoke(this, new DiffFileRequestedEventArgs(id, path, context));

    public void RaiseClosed(TerminalId id) => Closed?.Invoke(this, new DiffClosedEventArgs(id));

    public ValueTask ShowPendingAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        Record(new DiffViewCall("pending", terminalId));

    public ValueTask ShowIndexAsync(
        TerminalId terminalId,
        DiffIndex index,
        IReadOnlyList<GitWorktree> worktrees,
        string? note,
        IReadOnlyList<string> expandFiles,
        CancellationToken cancellationToken) =>
        Record(new DiffViewCall("index", terminalId, index, worktrees, note, expandFiles));

    public ValueTask ShowFileAsync(TerminalId terminalId, FileDiff file, CancellationToken cancellationToken) =>
        Record(new DiffViewCall("file", terminalId, File: file, Path: file.Path));

    public ValueTask ShowErrorAsync(TerminalId terminalId, string? path, string message, CancellationToken cancellationToken) =>
        Record(new DiffViewCall("error", terminalId, Path: path, Message: message));

    public ValueTask MarkStaleAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        Record(new DiffViewCall("stale", terminalId));

    private ValueTask Record(DiffViewCall call)
    {
        lock (_sync)
        {
            _calls.Add(call);
        }

        return OnCall?.Invoke(call) ?? ValueTask.CompletedTask;
    }
}

/// <summary>Координатор diff и его сигнал «устарело» на подделках — для тестов корневой ViewModel.</summary>
internal sealed class ShellDiffParts
{
    public ShellDiffParts(IHookListener? hooks = null)
    {
        Coordinator = new DiffCoordinator(Git, View, new InlineUiDispatcher());
        Tracker = new DiffStaleTracker(hooks ?? new FakeHookListener(), new InlineUiDispatcher(), Coordinator);
    }

    public FakeGitDiffReader Git { get; } = new();

    public FakeDiffView View { get; } = new();

    public DiffCoordinator Coordinator { get; }

    public DiffStaleTracker Tracker { get; }
}
