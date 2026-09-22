using ClaudeAgentsShell.App.Diff;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Координатор панели diff (issue #5): откуда берётся каталог, что уходит на страницу,
/// значок фоновой вкладки, отмена устаревших запросов, плашка «есть изменения».
/// </summary>
public sealed class DiffCoordinatorTests
{
    private const string ProjectPath = @"D:\src\alpha";
    private const string AgentPath = @"D:\src\alpha\.claude\worktrees\feature";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task По_клавише_diff_строится_от_текущего_каталога_агента()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        tab.CurrentDirectory = AgentPath;

        await harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);

        var request = Assert.Single(harness.Git.Requests);
        Assert.Equal(AgentPath, request.Directory);
        Assert.Null(request.BaseRef);
        Assert.Empty(request.Files);
        Assert.False(request.IgnoreWhitespace);
        Assert.Equal([AgentPath], harness.Git.WorktreeDirectories);
        Assert.Equal(["pending", "index"], harness.View.Calls.Select(call => call.Kind));

        var index = harness.View.CallsOf("index")[0];
        Assert.Equal(tab.TerminalId, index.TerminalId);
        Assert.Null(index.Note);
        Assert.Empty(index.ExpandFiles!);
        Assert.Single(index.Worktrees!);
    }

    [Fact]
    public async Task По_клавише_до_первого_хука_diff_строится_от_каталога_запуска()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        await harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);

        Assert.Equal(ProjectPath, Assert.Single(harness.Git.Requests).Directory);
    }

    [Fact]
    public async Task По_клавише_для_неизвестной_вкладки_ничего_не_делается()
    {
        await using var harness = new Harness();

        await harness.Coordinator.OpenForTabAsync(new TerminalId("ghost"), CancellationToken.None);

        Assert.Empty(harness.Git.Requests);
        Assert.Empty(harness.View.Calls);
    }

    [Fact]
    public async Task show_diff_с_неизвестным_токеном_отвечает_вкладка_не_найдена()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        var outcome = await harness.Coordinator.HandleAsync("tok-чужой", Request(), CancellationToken.None);

        Assert.IsType<ShowDiffOutcome.UnknownSession>(outcome);
        Assert.Empty(harness.Git.Requests);
        Assert.Empty(harness.View.Calls);
    }

    [Fact]
    public async Task show_diff_до_подключения_вкладок_отвечает_вкладка_не_найдена()
    {
        await using var harness = new Harness(start: false);

        var outcome = await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);

        Assert.IsType<ShowDiffOutcome.UnknownSession>(outcome);
    }

    [Fact]
    public async Task show_diff_отвечает_после_оглавления_числом_файлов_и_базой()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        tab.CurrentDirectory = AgentPath;

        var outcome = await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);

        var shown = Assert.IsType<ShowDiffOutcome.Shown>(outcome);
        Assert.Equal("Shown to the user: 2 files vs origin/main", shown.Summary);
        Assert.Equal(AgentPath, Assert.Single(harness.Git.Requests).Directory);
        Assert.False(tab.HasPendingDiff);
    }

    [Fact]
    public async Task show_diff_передаёт_базу_файлы_и_пояснение_на_страницу()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var outcome = await harness.Coordinator.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowDiffRequest("develop", Directory: null, ["src/a.cs", " "], "Поправил разбор"),
            CancellationToken.None);

        Assert.IsType<ShowDiffOutcome.Shown>(outcome);
        var request = Assert.Single(harness.Git.Requests);
        Assert.Equal("develop", request.BaseRef);
        Assert.Equal(["src/a.cs"], request.Files);

        var index = Assert.Single(harness.View.CallsOf("index"));
        Assert.Equal("Поправил разбор", index.Note);
        Assert.Equal(["src/a.cs"], index.ExpandFiles!);
        Assert.Equal(tab.TerminalId, index.TerminalId);
    }

    [Fact]
    public async Task show_diff_с_относительным_каталогом_считает_его_от_каталога_вкладки()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        await harness.Coordinator.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowDiffRequest(null, "sub", [], null),
            CancellationToken.None);

        Assert.Equal(Path.Combine(ProjectPath, "sub"), Assert.Single(harness.Git.Requests).Directory);
    }

    [Fact]
    public async Task show_diff_с_null_вместо_списка_файлов_не_падает()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        var outcome = await harness.Coordinator.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowDiffRequest(null, null, null!, null),
            CancellationToken.None);

        Assert.IsType<ShowDiffOutcome.Shown>(outcome);
        Assert.Empty(Assert.Single(harness.Git.Requests).Files);
    }

    [Fact]
    public async Task show_diff_в_фоновой_вкладке_ставит_значок_и_не_переключает_активную()
    {
        await using var harness = new Harness();
        var active = harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");

        var outcome = await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request(), CancellationToken.None);

        Assert.IsType<ShowDiffOutcome.Shown>(outcome);
        Assert.True(background.HasPendingDiff);
        Assert.False(background.IsActive);
        Assert.True(active.IsActive);
        Assert.False(active.HasPendingDiff);
        Assert.Equal(background.TerminalId, Assert.Single(harness.View.CallsOf("index")).TerminalId);

        // Переход во вкладку любым путём снимает значок.
        active.IsActive = false;
        background.IsActive = true;

        Assert.False(background.HasPendingDiff);
    }

    [Fact]
    public async Task show_diff_и_хуки_трогают_вкладки_только_в_потоке_интерфейса()
    {
        var dispatcher = new QueuedUiDispatcher();
        await using var harness = new Harness(dispatcher: dispatcher);
        harness.AddTab("t1", active: true);

        var outcome = Task.Run(() => harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None));
        await WaitUntilAsync(() => dispatcher.HasPending);

        Assert.Empty(harness.Git.Requests);
        dispatcher.Drain();
        Assert.IsType<ShowDiffOutcome.Shown>(await outcome.WaitAsync(Timeout));

        // Первая пачка — та, в которой шёл сам show_diff; вторая — настоящая правка.
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        Assert.Empty(harness.View.CallsOf("stale"));

        dispatcher.Drain();
        await harness.Coordinator.WhenIdleAsync();
        Assert.Single(harness.View.CallsOf("stale"));
    }

    [Fact]
    public async Task Ошибка_git_показывается_в_панели_и_уходит_агенту_отказом()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        harness.Git.ListChanges = (_, _) =>
            Task.FromException<DiffIndex>(new DiffUnavailableException(DiffFailure.NotARepository, "Каталог не в репозитории git."));

        var outcome = await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);

        var failed = Assert.IsType<ShowDiffOutcome.Failed>(outcome);
        Assert.Equal("Каталог не в репозитории git.", failed.Message);
        var error = Assert.Single(harness.View.CallsOf("error"));
        Assert.Null(error.Path);
        Assert.Equal("Каталог не в репозитории git.", error.Message);
        Assert.Empty(harness.View.CallsOf("index"));
    }

    [Fact]
    public async Task Запрос_файла_читает_его_из_последнего_оглавления_и_отправляет_на_страницу()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        harness.View.RaiseFile(tab.TerminalId, "src/b.cs", DiffContext.FullFile);
        await harness.Coordinator.WhenIdleAsync();

        Assert.Equal([("src/b.cs", DiffContext.FullFile)], harness.Git.FileReads);
        var file = Assert.Single(harness.View.CallsOf("file"));
        Assert.Equal("src/b.cs", file.File!.Path);
        Assert.Equal(DiffContext.FullFile, file.File.Context);
    }

    [Fact]
    public async Task Сбой_чтения_файла_показывается_у_файла()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");
        harness.Git.ReadFile = (_, _, _) =>
            Task.FromException<FileDiff>(new DiffUnavailableException(DiffFailure.Timeout, "git не уложился в 30 с."));

        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        await harness.Coordinator.WhenIdleAsync();

        var error = Assert.Single(harness.View.CallsOf("error"));
        Assert.Equal("src/a.cs", error.Path);
        Assert.Equal("git не уложился в 30 с.", error.Message);
        Assert.Empty(harness.View.CallsOf("file"));
    }

    [Fact]
    public async Task Файл_вне_оглавления_не_читается()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        harness.View.RaiseFile(tab.TerminalId, "нет/такого.cs");
        await harness.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.Git.FileReads);
        Assert.Equal("нет/такого.cs", Assert.Single(harness.View.CallsOf("error")).Path);
    }

    [Fact]
    public async Task Обновление_с_новой_базой_перестраивает_оглавление_с_прежними_файлами()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        await harness.Coordinator.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowDiffRequest(null, null, ["src/a.cs"], "Заметка"),
            CancellationToken.None);

        harness.View.RaiseRefresh(tab.TerminalId, directory: null, baseRef: "release", ignoreWhitespace: true);
        await harness.Coordinator.WhenIdleAsync();

        Assert.Equal(2, harness.Git.Requests.Count);
        var second = harness.Git.Requests[1];
        Assert.Equal(ProjectPath, second.Directory);
        Assert.Equal("release", second.BaseRef);
        Assert.True(second.IgnoreWhitespace);
        Assert.Equal(["src/a.cs"], second.Files);

        var index = harness.View.CallsOf("index")[1];
        Assert.Equal("release", index.Index!.BaseRef);
        Assert.Equal("Заметка", index.Note);
    }

    [Fact]
    public async Task Обновление_без_базы_и_каталога_берёт_прежние_и_меняет_рабочее_дерево_если_передано()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        await harness.Coordinator.HandleAsync(
            FakeDiffTabs.TokenFor("t1"),
            new ShowDiffRequest("develop", null, [], null),
            CancellationToken.None);

        harness.View.RaiseRefresh(tab.TerminalId, directory: AgentPath, baseRef: null, ignoreWhitespace: false);
        await harness.Coordinator.WhenIdleAsync();

        var second = harness.Git.Requests[1];
        Assert.Equal(AgentPath, second.Directory);
        Assert.Equal("develop", second.BaseRef);

        // Выбранное дерево запоминается: следующие обновления приходят с dir = null.
        harness.View.RaiseRefresh(tab.TerminalId, directory: null, baseRef: null, ignoreWhitespace: true);
        await harness.Coordinator.WhenIdleAsync();

        Assert.Equal(AgentPath, harness.Git.Requests[2].Directory);
        Assert.Equal("develop", harness.Git.Requests[2].BaseRef);
    }

    [Fact]
    public async Task Обновление_сначала_показывает_строится()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        harness.View.RaiseRefresh(tab.TerminalId, null, null, ignoreWhitespace: true);
        await harness.Coordinator.WhenIdleAsync();

        Assert.Equal(["pending", "index", "pending", "index"], harness.View.Calls.Select(call => call.Kind));
    }

    [Fact]
    public async Task Новый_запрос_отменяет_прежний_и_его_ответ_не_доходит_до_страницы()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var slow = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken slowToken = default;
        harness.Git.ListChanges = (_, token) =>
        {
            slowToken = token;
            return slow.Task;
        };

        var first = harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);

        harness.Git.ListChanges = null;
        harness.View.RaiseRefresh(tab.TerminalId, null, "second-base", ignoreWhitespace: false);
        await WaitUntilAsync(() => harness.View.CallsOf("index").Count == 1);

        Assert.True(slowToken.IsCancellationRequested);

        // Git не послушал отмену и всё-таки ответил — ответ списанного поколения не показывается.
        slow.SetResult(new DiffIndex(ProjectPath, "stale-base", "0", false, [FakeGitDiffReader.Entry("old.cs")]));
        var outcome = await first.WaitAsync(Timeout);

        Assert.IsType<ShowDiffOutcome.Shown>(outcome);
        var index = Assert.Single(harness.View.CallsOf("index"));
        Assert.Equal("second-base", index.Index!.BaseRef);
    }

    [Fact]
    public async Task Новый_запрос_отменяет_файловые_запросы_прежнего_оглавления()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        var blocked = new TaskCompletionSource<FileDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken fileToken = default;
        harness.Git.ReadFile = (_, _, token) =>
        {
            fileToken = token;
            return blocked.Task;
        };

        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        harness.Git.ReadFile = null;
        await harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);

        // Отмена загрузки связана с отменой поколения, а колбэки поколения исполняются в пуле —
        // поэтому до загрузки она доходит чуть позже; показ отсекает синхронная сверка поколения.
        await WaitUntilAsync(() => fileToken.IsCancellationRequested);
        blocked.SetResult(new FileDiff("src/a.cs", DiffContext.Hunks, "old", false));
        await harness.Coordinator.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Empty(harness.View.CallsOf("file"));

        // Новое оглавление страница уже получила — ошибки у файла прежнего быть не должно.
        Assert.Empty(harness.View.CallsOf("error"));
    }

    [Fact]
    public async Task Повторный_запрос_того_же_пути_отменяет_прежний_и_не_трогает_другие()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        var gates = new Dictionary<string, TaskCompletionSource>();
        var tokens = new List<(string Path, CancellationToken Token)>();
        harness.Git.ReadFile = async (file, context, token) =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates)
            {
                gates[file.Path + "#" + tokens.Count] = gate;
                tokens.Add((file.Path, token));
            }

            // Отмену фейк нарочно не слушает: проверяется, что поздний ответ не уходит на страницу.
            await gate.Task;
            return new FileDiff(file.Path, context, file.Path + "#" + context, false);
        };

        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        harness.View.RaiseFile(tab.TerminalId, "src/b.cs");
        harness.View.RaiseFile(tab.TerminalId, "src/a.cs", DiffContext.FullFile);
        await WaitUntilAsync(() => tokens.Count == 3);

        Assert.True(tokens[0].Token.IsCancellationRequested);
        Assert.False(tokens[1].Token.IsCancellationRequested);
        Assert.False(tokens[2].Token.IsCancellationRequested);

        foreach (var gate in gates.Values)
        {
            gate.SetResult();
        }

        await harness.Coordinator.WhenIdleAsync().WaitAsync(Timeout);

        var shown = harness.View.CallsOf("file").Select(call => call.File!.Text).OrderBy(text => text, StringComparer.Ordinal);
        Assert.Equal(["src/a.cs#FullFile", "src/b.cs#Hunks"], shown);
        Assert.Empty(harness.View.CallsOf("error"));
    }

    [Fact]
    public async Task Прерванное_не_по_нашей_воле_чтение_файла_даёт_ошибку_у_файла()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");
        harness.Git.ReadFile = (_, _, _) => Task.FromException<FileDiff>(new OperationCanceledException("git timeout"));

        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        await harness.Coordinator.WhenIdleAsync();

        var error = Assert.Single(harness.View.CallsOf("error"));
        Assert.Equal("src/a.cs", error.Path);
    }

    [Fact]
    public async Task Закрытие_панели_отменяет_построение_и_забывает_оглавление()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var slow = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken slowToken = default;
        harness.Git.ListChanges = (_, token) =>
        {
            slowToken = token;
            return slow.Task;
        };

        var building = harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
        harness.View.RaiseClosed(tab.TerminalId);

        Assert.True(slowToken.IsCancellationRequested);
        slow.SetResult(FakeGitDiffReader.IndexFor(new DiffRequest(ProjectPath, null, [], false), harness.Git.DefaultFiles));
        await building.WaitAsync(Timeout);

        Assert.Empty(harness.View.CallsOf("index"));

        // Оглавления больше нет: запрос файла не идёт в git.
        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        await harness.Coordinator.WhenIdleAsync();
        Assert.Empty(harness.Git.FileReads);
    }

    [Fact]
    public async Task Закрытие_вкладки_снимает_подписку_значка_и_состояние()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);
        var background = harness.AddTab("t2");
        await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t2"), Request(), CancellationToken.None);

        harness.Tabs.Close(background);
        background.IsActive = true;

        // Вкладки уже нет — значок никто не снимает, и плашка ей тоже не приходит.
        Assert.True(background.HasPendingDiff);
        harness.View.RaiseFile(background.TerminalId, "src/a.cs");
        await harness.Coordinator.WhenIdleAsync();
        Assert.Empty(harness.Git.FileReads);
    }

    [Fact]
    public async Task Одновременных_чтений_файлов_не_больше_потолка()
    {
        await using var harness = new Harness();
        var files = Enumerable.Range(0, 8).Select(i => FakeGitDiffReader.Entry($"f{i}.cs")).ToArray();
        harness.Git.DefaultFiles = files;
        var tab = await harness.OpenAsync("t1");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Git.ReadFile = async (file, context, token) =>
        {
            await release.Task.WaitAsync(token);
            return new FileDiff(file.Path, context, "x", false);
        };

        foreach (var file in files)
        {
            harness.View.RaiseFile(tab.TerminalId, file.Path);
        }

        await WaitUntilAsync(() => harness.Git.FileReads.Count >= DiffCoordinator.MaxParallelFileReads);
        Assert.Equal(DiffCoordinator.MaxParallelFileReads, harness.Git.FileReads.Count);

        release.SetResult();
        await harness.Coordinator.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal(DiffCoordinator.MaxParallelFileReads, harness.Git.MaxInFlight);
        Assert.Equal(files.Length, harness.View.CallsOf("file").Count);
    }

    [Fact]
    public async Task PostToolBatch_при_открытой_панели_даёт_одну_плашку_до_обновления()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"), agentId: "sub-1");
        harness.Hooks.Raise(HookKind.Stop, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Single(harness.View.CallsOf("stale"));

        // Обновление — новый снимок: следующая правка снова даёт плашку.
        harness.View.RaiseRefresh(tab.TerminalId, null, null, false);
        await harness.Coordinator.WhenIdleAsync();
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"), agentId: "sub-1");
        await harness.Coordinator.WhenIdleAsync();

        Assert.Equal(2, harness.View.CallsOf("stale").Count);
        Assert.Equal(2, harness.Git.Requests.Count);
    }

    [Fact]
    public async Task Пачка_с_самим_show_diff_и_конец_хода_плашку_не_дают()
    {
        await using var harness = new Harness();
        harness.AddTab("t1", active: true);

        await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        harness.Hooks.Raise(HookKind.Stop, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.View.CallsOf("stale"));

        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Single(harness.View.CallsOf("stale"));
    }

    [Fact]
    public async Task Обновление_панели_агента_не_пропускает_первую_пачку()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);
        await harness.Coordinator.HandleAsync(FakeDiffTabs.TokenFor("t1"), Request(), CancellationToken.None);

        harness.View.RaiseRefresh(tab.TerminalId, null, null, false);
        await harness.Coordinator.WhenIdleAsync();
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Single(harness.View.CallsOf("stale"));
    }

    [Fact]
    public async Task Конец_хода_без_пачки_плашку_не_даёт()
    {
        await using var harness = new Harness();
        await harness.OpenAsync("t1");

        harness.Hooks.Raise(HookKind.Stop, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.View.CallsOf("stale"));
    }

    [Fact]
    public async Task Хуки_без_открытой_панели_и_чужих_видов_плашку_не_дают()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
        harness.Hooks.Raise(HookKind.UserPromptSubmit, FakeDiffTabs.TokenFor("t1"));
        harness.Hooks.Raise(HookKind.PermissionRequest, FakeDiffTabs.TokenFor("t1"));
        harness.Hooks.Raise(HookKind.PostToolBatch, "tok-чужой");
        await harness.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.View.CallsOf("stale"));

        harness.View.RaiseClosed(tab.TerminalId);
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        await harness.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.View.CallsOf("stale"));
    }

    [Fact]
    public async Task Правка_во_время_построения_даёт_плашку_сразу_после_оглавления()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var slow = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Git.ListChanges = (_, _) => slow.Task;

        var building = harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
        harness.Hooks.Raise(HookKind.PostToolBatch, FakeDiffTabs.TokenFor("t1"));
        slow.SetResult(FakeGitDiffReader.IndexFor(new DiffRequest(ProjectPath, null, [], false), harness.Git.DefaultFiles));
        await building.WaitAsync(Timeout);

        Assert.Equal(["pending", "index", "stale"], harness.View.Calls.Select(call => call.Kind));
    }

    [Fact]
    public async Task Освобождение_снимает_подписки_и_отменяет_работу()
    {
        var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var slow = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken slowToken = default;
        harness.Git.ListChanges = (_, token) =>
        {
            slowToken = token;
            return slow.Task.WaitAsync(token);
        };
        var building = harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);

        await harness.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.True(slowToken.IsCancellationRequested);
        await building.WaitAsync(Timeout);
        Assert.False(harness.View.HasSubscribers);
    }

    [Fact]
    public async Task Закрытие_панели_не_исполняет_колбэки_отмены_на_вызывающем_потоке()
    {
        await using var harness = new Harness();
        var tab = harness.AddTab("t1", active: true);

        var probe = new CancellationProbe();
        var slow = new TaskCompletionSource<DiffIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Git.ListChanges = (_, token) =>
        {
            probe.Watch(token);
            return slow.Task.WaitAsync(token);
        };

        var building = harness.Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
        probe.Run(() => harness.View.RaiseClosed(tab.TerminalId));

        // Токен отменён сразу — сверка поколения от колбэков не зависит.
        Assert.True(probe.Token.IsCancellationRequested);
        await probe.CallbackRan.WaitAsync(Timeout);
        Assert.False(probe.RanInline);

        await building.WaitAsync(Timeout);
        Assert.Empty(harness.View.CallsOf("index"));
    }

    [Fact]
    public async Task Повторный_запрос_пути_не_исполняет_колбэки_отмены_на_вызывающем_потоке()
    {
        await using var harness = new Harness();
        var tab = await harness.OpenAsync("t1");

        var probe = new CancellationProbe();
        var blocked = new TaskCompletionSource<FileDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Git.ReadFile = (_, _, token) =>
        {
            probe.Watch(token);
            return blocked.Task.WaitAsync(token);
        };

        harness.View.RaiseFile(tab.TerminalId, "src/a.cs");
        harness.Git.ReadFile = null;
        probe.Run(() => harness.View.RaiseFile(tab.TerminalId, "src/a.cs", DiffContext.FullFile));

        Assert.True(probe.Token.IsCancellationRequested);
        await probe.CallbackRan.WaitAsync(Timeout);
        Assert.False(probe.RanInline);

        await harness.Coordinator.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(DiffContext.FullFile, Assert.Single(harness.View.CallsOf("file")).File!.Context);
        Assert.Empty(harness.View.CallsOf("error"));
    }

    private static ShowDiffRequest Request() => new(null, null, [], null);

    /// <summary>
    /// Колбэк отмены вместо процесса git: запоминает, исполнился ли он прямо внутри вызова,
    /// который отменял работу (в приложении это был бы поток интерфейса).
    /// </summary>
    private sealed class CancellationProbe
    {
        private readonly TaskCompletionSource _ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runningThread = -1;

        public CancellationToken Token { get; private set; }

        public bool RanInline { get; private set; }

        public Task CallbackRan => _ran.Task;

        public void Watch(CancellationToken token)
        {
            Token = token;
            token.Register(() =>
            {
                RanInline = Volatile.Read(ref _runningThread) == Environment.CurrentManagedThreadId;
                _ran.TrySetResult();
            });
        }

        public void Run(Action cancel)
        {
            Volatile.Write(ref _runningThread, Environment.CurrentManagedThreadId);
            try
            {
                cancel();
            }
            finally
            {
                Volatile.Write(ref _runningThread, -1);
            }
        }
    }

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
        private readonly DiffStaleTracker _tracker;

        public Harness(bool start = true, IUiDispatcher? dispatcher = null)
        {
            dispatcher ??= new InlineUiDispatcher();
            Coordinator = new DiffCoordinator(Git, View, dispatcher);
            _tracker = new DiffStaleTracker(Hooks, dispatcher, Coordinator);
            if (start)
            {
                Coordinator.Start(Tabs);
                _tracker.Start();
            }
        }

        public FakeDiffTabs Tabs { get; } = new();

        public FakeGitDiffReader Git { get; } = new();

        public FakeDiffView View { get; } = new();

        public FakeHookListener Hooks { get; } = new();

        public DiffCoordinator Coordinator { get; }

        public TabViewModel AddTab(string id, bool active = false) => Tabs.Add(id, ProjectPath, active);

        /// <summary>Вкладка с уже открытой панелью.</summary>
        public async Task<TabViewModel> OpenAsync(string id)
        {
            var tab = AddTab(id, active: true);
            await Coordinator.OpenForTabAsync(tab.TerminalId, CancellationToken.None);
            return tab;
        }

        public async ValueTask DisposeAsync()
        {
            _tracker.Dispose();
            await Coordinator.DisposeAsync();
        }
    }
}
