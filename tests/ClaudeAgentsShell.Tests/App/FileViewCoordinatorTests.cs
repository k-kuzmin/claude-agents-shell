using ClaudeAgentsShell.App.Diff;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Координатор <c>show_file</c>: каталог, чтение с потолками, итог для агента, значок фоновой
/// вкладки, отмена и то, как он делит панель вкладки с diff.
/// </summary>
public sealed class FileViewCoordinatorTests
{
    private const string ProjectPath = @"D:\src\alpha";
    private const string AgentPath = @"D:\src\alpha\.claude\worktrees\feature";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Неизвестный_токен_отвечает_вкладка_не_найдена()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        var outcome = await harness.Files.HandleAsync("tok-чужой", Request("a.cs"), CancellationToken.None);

        Assert.IsType<ShowFileOutcome.UnknownSession>(outcome);
        Assert.Empty(harness.Reader.RootRequests);
        Assert.Empty(harness.FileView.Shown);
    }

    [Fact]
    public async Task До_запуска_отвечает_вкладка_не_найдена()
    {
        await using var harness = new Harness(start: false);
        harness.AddTab("t1", active: true);

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        Assert.IsType<ShowFileOutcome.UnknownSession>(outcome);
    }

    [Fact]
    public async Task Каталог_по_умолчанию_текущий_каталог_агента_иначе_каталог_запуска()
    {
        await using var harness = new Harness();
        var agent = harness.AddTab("t1", active: true);
        agent.CurrentDirectory = AgentPath;
        harness.AddTab("t2");

        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);

        Assert.Equal([AgentPath, ProjectPath], harness.Reader.RootRequests);
        Assert.Equal([AgentPath, ProjectPath], harness.Reader.Reads.Select(read => read.Directory));
    }

    [Fact]
    public async Task Относительный_каталог_считается_от_каталога_вкладки()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowFileRequest("src", [new ShowFileItem("a.cs", null)], null),
            CancellationToken.None);

        Assert.Equal([ProjectPath + @"\src"], harness.Reader.RootRequests);
    }

    [Fact]
    public async Task Нет_каталога_отказ_с_причиной_и_панель_не_трогается()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.Reader.Root = null;

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        var failed = Assert.IsType<ShowFileOutcome.Failed>(outcome);
        Assert.Contains(ProjectPath, failed.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Reader.Reads);
        Assert.Empty(harness.FileView.Shown);
    }

    [Fact]
    public async Task Файлы_читаются_параллельно_с_потолком_и_уходят_в_порядке_агента()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Read = async (item, token) =>
        {
            await release.Task.WaitAsync(token);

            // Первые по порядку отвечают последними.
            await Task.Delay(item.Path == "f0.cs" ? 50 : 0, token);
            return FakeWorkspaceFileReader.Text(item, item.Path);
        };

        var paths = Enumerable.Range(0, 7).Select(i => $"f{i}.cs").ToArray();
        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(paths), CancellationToken.None);

        await WaitUntilAsync(() => harness.Reader.Reads.Count >= FileViewCoordinator.MaxParallelReads);
        Assert.Equal(FileViewCoordinator.MaxParallelReads, harness.Reader.Reads.Count);
        release.SetResult();

        var outcome = await call;

        Assert.IsType<ShowFileOutcome.Shown>(outcome);
        Assert.Equal(FileViewCoordinator.MaxParallelReads, harness.Reader.MaxInFlight);
        var (terminalId, files) = Assert.Single(harness.FileView.Shown);
        Assert.Equal(new TerminalId("t1"), terminalId);
        Assert.Equal(paths, files.Files.Select(file => file.Path));
        Assert.Equal(paths, files.Files.Select(file => file.Text));
        Assert.Equal(ProjectPath, files.Root);
    }

    [Fact]
    public async Task Пояснение_и_диапазон_строк_доходят_до_панели_и_в_итог()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        var outcome = await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowFileRequest(null, [new ShowFileItem("a.cs", new LineRange(10, 20)), new ShowFileItem("b.cs", null)], "  Глянь сюда "),
            CancellationToken.None);

        var shown = Assert.IsType<ShowFileOutcome.Shown>(outcome);
        Assert.Equal("Shown to the user: 2 files (a.cs lines 10-20, b.cs).", shown.Summary);
        var files = Assert.Single(harness.FileView.Shown).Files;
        Assert.Equal("  Глянь сюда ", files.Note);
        Assert.Equal(new LineRange(10, 20), files.Files[0].Focus);
    }

    [Fact]
    public async Task Пустое_пояснение_не_доходит_до_панели()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowFileRequest(null, [new ShowFileItem("a.cs", null)], "   "),
            CancellationToken.None);

        Assert.Null(Assert.Single(harness.FileView.Shown).Files.Note);
    }

    [Fact]
    public async Task Все_файлы_с_проблемой_отказ_с_перечислением_и_без_панели_и_значка()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");
        harness.Reader.Read = (item, _) => Task.FromResult(FakeWorkspaceFileReader.Problem(
            item,
            item.Path == "gone.cs" ? ViewedFileProblem.NotFound : ViewedFileProblem.Binary));

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("gone.cs", "logo.png"), CancellationToken.None);

        var failed = Assert.IsType<ShowFileOutcome.Failed>(outcome);
        Assert.Equal("None of the files could be shown: gone.cs - not found or is a directory; logo.png - binary file.", failed.Message);
        Assert.Empty(harness.FileView.Shown);
        Assert.False(background.HasPendingDiff);
    }

    [Fact]
    public async Task Часть_файлов_с_проблемой_показ_с_перечислением_проблем()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.Reader.Read = (item, _) => Task.FromResult(item.Path switch
        {
            "big.log" => FakeWorkspaceFileReader.Problem(item, ViewedFileProblem.TooLarge),
            "../x.cs" => FakeWorkspaceFileReader.Problem(item, ViewedFileProblem.OutsideRoot),
            "locked.cs" => FakeWorkspaceFileReader.Problem(item, ViewedFileProblem.Unreadable),
            _ => FakeWorkspaceFileReader.Text(item, "text"),
        });

        var outcome = await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            Request("a.cs", "big.log", "../x.cs", "locked.cs"),
            CancellationToken.None);

        var shown = Assert.IsType<ShowFileOutcome.Shown>(outcome);
        Assert.Equal(
            "Shown to the user: 1 file (a.cs). Not shown: big.log - larger than the 4 MB limit; "
                + "../x.cs - outside the workspace root; locked.cs - could not be read (locked or access denied).",
            shown.Summary);

        // Файлы с проблемой тоже уходят на страницу — там видно, почему их нет.
        Assert.Equal(4, Assert.Single(harness.FileView.Shown).Files.Files.Count);
    }

    [Fact]
    public async Task Брошенное_читателем_становится_нечитаемым_файлом()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.Reader.Read = (item, _) => item.Path.EndsWith("bad.cs", StringComparison.Ordinal)
            ? throw new IOException("занят")
            : Task.FromResult(FakeWorkspaceFileReader.Text(item, "text"));

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs", @"dir\bad.cs"), CancellationToken.None);

        Assert.IsType<ShowFileOutcome.Shown>(outcome);
        var bad = Assert.Single(harness.FileView.Shown).Files.Files[1];
        Assert.Equal(ViewedFileProblem.Unreadable, bad.Problem);
        Assert.Equal("dir/bad.cs", bad.Path);
    }

    [Fact]
    public async Task Путь_непрочитанного_файла_считается_от_корня()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.Reader.Read = (_, _) => throw new IOException("занят");

        await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowFileRequest("src", [new ShowFileItem(ProjectPath + @"\src\a.cs", null), new ShowFileItem("b.cs", null)], null),
            CancellationToken.None);

        // Все файлы с проблемой — показа нет; путь видно в отказе.
        Assert.Empty(harness.FileView.Shown);
        harness.Reader.Read = (item, _) => item.Path == "ok.cs"
            ? Task.FromResult(FakeWorkspaceFileReader.Text(item, "text"))
            : throw new IOException("занят");

        await harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowFileRequest("src", [new ShowFileItem(ProjectPath + @"\src\a.cs", null), new ShowFileItem("b.cs", null), new ShowFileItem("ok.cs", null)], null),
            CancellationToken.None);

        var files = Assert.Single(harness.FileView.Shown).Files.Files;
        Assert.Equal(["src/a.cs", "src/b.cs", "ok.cs"], files.Select(file => file.Path));
    }

    [Fact]
    public async Task Освобождение_diff_дожидается_отправки_файлов()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.FileView.OnShow = _ => new ValueTask(release.Task);

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.FileView.Shown.Count == 1);

        var disposal = harness.Diff.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);

        release.SetResult();
        await disposal;
        Assert.IsType<ShowFileOutcome.Shown>(await call);
    }

    [Fact]
    public async Task Сверх_общего_потолка_файлы_помечаются_слишком_большими_а_после_него_не_читаются()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var huge = new string('x', (int)FileViewCoordinator.MaxTotalChars + 1);
        var rest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Read = async (item, token) =>
        {
            if (item.Path == "huge.txt")
            {
                return FakeWorkspaceFileReader.Text(item, huge);
            }

            await rest.Task.WaitAsync(token);
            return FakeWorkspaceFileReader.Text(item, "small");
        };

        // Малые файлы висят, огромный отвечает сразу и освобождает своё место пятому —
        // а счёт уже над потолком, и пятый не читается.
        var call = harness.Files.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            Request("a.cs", "b.cs", "c.cs", "huge.txt", "late.cs"),
            CancellationToken.None);

        await WaitUntilAsync(() => harness.Reader.Reads.Count >= FileViewCoordinator.MaxParallelReads);
        rest.SetResult();

        var shown = Assert.IsType<ShowFileOutcome.Shown>(await call);

        Assert.DoesNotContain(harness.Reader.Reads, read => read.Item.Path == "late.cs");
        var files = Assert.Single(harness.FileView.Shown).Files.Files;
        Assert.Equal(
            [ViewedFileProblem.None, ViewedFileProblem.None, ViewedFileProblem.None, ViewedFileProblem.TooLarge, ViewedFileProblem.TooLarge],
            files.Select(file => file.Problem));
        Assert.Null(files[3].Text);
        Assert.Equal(
            "Shown to the user: 3 files (a.cs, b.cs, c.cs). Not shown: "
                + "huge.txt - over the 16 MB total for one call, show it separately; "
                + "late.cs - over the 16 MB total for one call, show it separately.",
            shown.Summary);
    }

    [Fact]
    public async Task Общий_потолок_применяется_по_порядку_агента()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var half = new string('x', (int)(FileViewCoordinator.MaxTotalChars / 2));
        harness.Reader.Read = (item, _) => Task.FromResult(FakeWorkspaceFileReader.Text(item, item.Path == "tiny.cs" ? "t" : half));

        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.txt", "b.txt", "c.txt", "tiny.cs"), CancellationToken.None);

        var files = Assert.Single(harness.FileView.Shown).Files.Files;
        Assert.Equal(
            [ViewedFileProblem.None, ViewedFileProblem.None, ViewedFileProblem.TooLarge, ViewedFileProblem.TooLarge],
            files.Select(file => file.Problem));
    }

    [Fact]
    public async Task Фоновая_вкладка_получает_значок_и_не_становится_активной()
    {
        await using var harness = new Harness();
        var active = harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);

        Assert.IsType<ShowFileOutcome.Shown>(outcome);
        Assert.Equal(background.TerminalId, Assert.Single(harness.FileView.Shown).TerminalId);
        Assert.True(background.HasPendingDiff);
        Assert.False(background.IsActive);
        Assert.True(active.IsActive);
        Assert.False(active.HasPendingDiff);

        active.IsActive = false;
        background.IsActive = true;

        Assert.False(background.HasPendingDiff);
    }

    [Fact]
    public async Task Активная_вкладка_значка_не_получает()
    {
        await using var harness = new Harness();
        var active = harness.AddTab("t1", active: true);

        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        Assert.False(active.HasPendingDiff);
        Assert.Single(harness.FileView.Shown);
    }

    [Fact]
    public async Task Значок_от_diff_и_от_файлов_один_и_снимается_один_раз()
    {
        await using var harness = new Harness();
        var active = harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");

        await harness.Diff.HandleAsync(FakeDiffTabs.TokenFor("t2"), new ShowDiffRequest(null, null, [], null), CancellationToken.None);
        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);
        Assert.True(background.HasPendingDiff);

        active.IsActive = false;
        background.IsActive = true;
        Assert.False(background.HasPendingDiff);

        // Значок снят и забыт: новый показ в фоне ставит его снова.
        background.IsActive = false;
        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);
        Assert.True(background.HasPendingDiff);
    }

    [Fact]
    public async Task Вкладки_трогаются_в_потоке_интерфейса_а_чтение_вне_его()
    {
        var dispatcher = new QueuedUiDispatcher();
        await using var harness = new Harness(dispatcher: dispatcher);
        harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);

        // Пока поток интерфейса не отработал, вкладка не найдена и ничего не читается.
        Assert.Empty(harness.Reader.RootRequests);
        dispatcher.Drain();

        var outcome = await call;
        Assert.IsType<ShowFileOutcome.Shown>(outcome);
        Assert.Single(harness.FileView.Shown);

        // Значок — тоже в потоке интерфейса.
        Assert.False(background.HasPendingDiff);
        dispatcher.Drain();
        Assert.True(background.HasPendingDiff);
    }

    [Fact]
    public async Task После_файлов_diff_не_шлёт_устарело_и_файлы_прежнего_оглавления()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenDiffAsync("t1");
        var before = harness.DiffView.Calls.Count;

        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        harness.Diff.NotifyFilesChanged(FakeDiffTabs.TokenFor("t1"));
        harness.DiffView.RaiseFile(tab.TerminalId, "src/a.cs");
        await harness.Diff.WhenIdleAsync();

        Assert.Equal(before, harness.DiffView.Calls.Count);
        Assert.Empty(harness.Git.FileReads);
    }

    [Fact]
    public async Task После_файлов_кнопка_diff_строит_diff_заново()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenDiffAsync("t1");
        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        await harness.Diff.OpenForTabAsync(tab.TerminalId, CancellationToken.None);

        Assert.Equal(["pending", "index", "pending", "index"], harness.DiffView.Calls.Select(call => call.Kind));
        Assert.Equal(2, harness.Git.Requests.Count);

        // И новое поколение снова живое: плашка «устарело» доходит.
        harness.Diff.NotifyFilesChanged(FakeDiffTabs.TokenFor("t1"));
        await harness.Diff.WhenIdleAsync();
        Assert.Equal("stale", harness.DiffView.Calls[^1].Kind);
    }

    [Fact]
    public async Task Файлы_сменяют_строящийся_show_diff_и_его_оглавление_не_уходит()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var git = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Git.ListChanges = (_, token) => git.Task.WaitAsync(token);

        var diffCall = harness.Diff.HandleAsync(FakeDiffTabs.TokenFor("t1"), new ShowDiffRequest(null, null, [], null), CancellationToken.None);
        await WaitUntilAsync(() => harness.Git.Requests.Count == 1);

        var files = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        Assert.IsType<ShowFileOutcome.Shown>(files);
        var diff = Assert.IsType<ShowDiffOutcome.Shown>(await diffCall);
        Assert.Contains("replaced", diff.Summary, StringComparison.Ordinal);
        Assert.Equal(["pending"], harness.DiffView.Calls.Select(call => call.Kind));
        Assert.Single(harness.FileView.Shown);
    }

    [Fact]
    public async Task Diff_открытый_во_время_чтения_файлов_побеждает()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Read = async (item, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);

        await harness.Diff.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
        release.SetResult();

        var outcome = Assert.IsType<ShowFileOutcome.Shown>(await call);
        Assert.Contains("replaced", outcome.Summary, StringComparison.Ordinal);
        Assert.Empty(harness.FileView.Shown);

        // Diff цел.
        harness.Diff.NotifyFilesChanged(FakeDiffTabs.TokenFor("t1"));
        await harness.Diff.WhenIdleAsync();
        Assert.Equal(["pending", "index", "stale"], harness.DiffView.Calls.Select(c => c.Kind));
    }

    [Fact]
    public async Task Diff_открытый_раньше_вызова_файлов_уступает_им()
    {
        await using var harness = new Harness();
        await harness.OpenDiffAsync("t1");

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);

        Assert.StartsWith("Shown to the user", Assert.IsType<ShowFileOutcome.Shown>(outcome).Summary, StringComparison.Ordinal);
        Assert.Single(harness.FileView.Shown);
    }

    [Fact]
    public async Task Повторный_вызов_в_ту_же_вкладку_отменяет_незавершённый()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var firstCancelled = false;
        harness.Reader.Read = async (item, token) =>
        {
            if (item.Path == "first.cs")
            {
                try
                {
                    await Task.Delay(Timeout, token);
                }
                catch (OperationCanceledException)
                {
                    firstCancelled = true;
                    throw;
                }
            }

            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var first = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("first.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);

        var second = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("second.cs"), CancellationToken.None);

        Assert.StartsWith("Shown to the user", Assert.IsType<ShowFileOutcome.Shown>(second).Summary, StringComparison.Ordinal);
        Assert.Contains("replaced", Assert.IsType<ShowFileOutcome.Shown>(await first).Summary, StringComparison.Ordinal);
        Assert.True(firstCancelled);
        Assert.Equal("second.cs", Assert.Single(Assert.Single(harness.FileView.Shown).Files.Files).Path);
    }

    [Fact]
    public async Task Вызов_в_другую_вкладку_не_отменяет_незавершённый()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.AddTab("t2");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Read = async (item, token) =>
        {
            if (item.Path == "slow.cs")
            {
                await release.Task.WaitAsync(token);
            }

            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var first = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("slow.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);
        await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);
        release.SetResult();

        Assert.StartsWith("Shown to the user", Assert.IsType<ShowFileOutcome.Shown>(await first).Summary, StringComparison.Ordinal);
        Assert.Equal(2, harness.FileView.Shown.Count);
    }

    [Fact]
    public async Task Закрытие_панели_во_время_чтения_отменяет_показ()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        harness.Reader.Read = async (item, token) =>
        {
            await Task.Delay(Timeout, token);
            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);
        harness.DiffView.RaiseClosed(tab.TerminalId);

        Assert.Contains("closed", Assert.IsType<ShowFileOutcome.Shown>(await call).Summary, StringComparison.Ordinal);
        Assert.Empty(harness.FileView.Shown);
    }

    [Fact]
    public async Task Закрытие_вкладки_во_время_чтения_отменяет_показ()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        harness.Reader.Read = async (item, token) =>
        {
            await Task.Delay(Timeout, token);
            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);
        harness.Tabs.Close(tab);

        Assert.IsType<ShowFileOutcome.Shown>(await call);
        Assert.Empty(harness.FileView.Shown);
        await harness.Files.WhenIdleAsync();
    }

    [Fact]
    public async Task Отмена_ожидания_вызова_не_отменяет_показ()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Read = async (item, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeWorkspaceFileReader.Text(item, "text");
        };
        using var cancellation = new CancellationTokenSource();

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), cancellation.Token);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        release.SetResult();
        await harness.Files.WhenIdleAsync();

        Assert.Single(harness.FileView.Shown);
    }

    [Fact]
    public async Task Сбой_страницы_отказ_без_значка()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");
        harness.FileView.OnShow = _ => throw new InvalidOperationException("мост закрыт");

        var outcome = await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request("a.cs"), CancellationToken.None);

        Assert.Contains("мост закрыт", Assert.IsType<ShowFileOutcome.Failed>(outcome).Message, StringComparison.Ordinal);
        Assert.False(background.HasPendingDiff);
    }

    [Fact]
    public async Task Освобождение_отменяет_незавершённые_вызовы_и_дожидается_их()
    {
        var harness = new Harness();
        harness.AddTab("t1", active: true);
        var cancelled = false;
        harness.Reader.Read = async (item, token) =>
        {
            try
            {
                await Task.Delay(Timeout, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return FakeWorkspaceFileReader.Text(item, "text");
        };

        var call = harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None);
        await WaitUntilAsync(() => harness.Reader.Reads.Count == 1);

        await harness.DisposeAsync();

        Assert.True(cancelled);
        Assert.True(call.IsCompleted);
        Assert.IsType<ShowFileOutcome.Shown>(await call);
        Assert.Empty(harness.FileView.Shown);
        Assert.False(harness.DiffView.HasSubscribers);

        // После освобождения вкладок уже нет.
        Assert.IsType<ShowFileOutcome.UnknownSession>(
            await harness.Files.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request("a.cs"), CancellationToken.None));
    }

    private static ShowFileRequest Request(params string[] paths) =>
        new(null, [.. paths.Select(path => new ShowFileItem(path, null))], null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Условие не наступило вовремя.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private bool _disposed;

        public Harness(bool start = true, IUiDispatcher? dispatcher = null)
        {
            dispatcher ??= new InlineUiDispatcher();
            Diff = new DiffCoordinator(Git, DiffView, dispatcher);
            Files = new FileViewCoordinator(Reader, FileView, DiffView, Diff, dispatcher);
            if (start)
            {
                Diff.Start(Tabs);
                Files.Start(Tabs);
            }
        }

        public FakeDiffTabs Tabs { get; } = new();

        public FakeGitDiffReader Git { get; } = new();

        public FakeDiffView DiffView { get; } = new();

        public FakeWorkspaceFileReader Reader { get; } = new();

        public FakeFileView FileView { get; } = new();

        public DiffCoordinator Diff { get; }

        public FileViewCoordinator Files { get; }

        public TabViewModel AddTab(string id, bool active = false) => Tabs.Add(id, ProjectPath, active);

        /// <summary>Активная вкладка с открытой панелью diff.</summary>
        public async Task<TabViewModel> OpenDiffAsync(string id)
        {
            var tab = AddTab(id, active: true);
            await Diff.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
            return tab;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Files.DisposeAsync();
            await Diff.DisposeAsync();
        }
    }
}
