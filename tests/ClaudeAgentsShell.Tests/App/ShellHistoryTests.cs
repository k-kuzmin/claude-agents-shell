using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Окно истории и «продолжить последнюю» со строки проекта (этап M3): что уходит в окно
/// и что оболочка делает с выбором — переключает вкладку или запускает сессию.
/// </summary>
public sealed class ShellHistoryTests
{
    private static ProjectDefinition Project(string name, int order) =>
        new(Guid.NewGuid(), name, @"D:\src\" + name, ShellKind.Pwsh, PreLaunch: null, ExtraArgs: [], Order: order);

    private sealed class Harness
    {
        public Harness(params ProjectDefinition[] projects)
        {
            Store.Seed(projects);
            foreach (var project in projects)
            {
                Probe.Add(project.Path);
            }

            var list = new ProjectListViewModel(
                Store,
                new FakeGitBranchReader(),
                new FakeGitBranchWatcher(),
                Probe,
                new FakeFolderPicker(),
                new FakeProjectSettingsDialog(),
                Prompt,
                new FakeShellLauncher(),
                new InlineUiDispatcher());
            var sessionState = new SessionStateCoordinator(
                new FakeHookListener(), Workspace, new FakeSessionHistoryReader(), new InlineUiDispatcher());
            Shell = new ShellViewModel(
                Workspace, list, Prompt, new InlineUiDispatcher(), sessionState, new FakeLayoutStore().CreateService(),
                Diff.Coordinator, Diff.Tracker, new FakeAppVersion("0.0.0"), History, HistoryReader);
        }

        public FakeProjectStore Store { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public ShellDiffParts Diff { get; } = new();

        public LaunchRecordingWorkspace Workspace { get; } = new();

        public FakeSessionHistoryDialog History { get; } = new();

        public ScriptedHistoryReader HistoryReader { get; } = new();

        public ShellViewModel Shell { get; }

        public ProjectRowViewModel Row(int index) => Shell.Projects.Rows[index];
    }

    private static async Task<Harness> StartedAsync(params ProjectDefinition[] projects)
    {
        var harness = new Harness(projects);
        await harness.Shell.InitializeAsync(CancellationToken.None);
        return harness;
    }

    private static async Task<TabViewModel> OpenWithSessionAsync(Harness harness, int row, string sessionId)
    {
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(row), CancellationToken.None);
        Assert.NotNull(tab);
        tab!.SessionId = sessionId;
        return tab;
    }

    [Fact]
    public async Task Request_carries_the_row_project_and_live_sessions()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = await StartedAsync(alpha, beta);
        await OpenWithSessionAsync(harness, 0, "a-1");
        await OpenWithSessionAsync(harness, 1, "b-1");
        var ended = await OpenWithSessionAsync(harness, 1, "b-2");
        ended.SessionEnded = true;
        var dead = await OpenWithSessionAsync(harness, 0, "a-2");
        dead.MarkExited(1);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.ShowHistoryAsync(harness.Row(1), CancellationToken.None);

        var request = Assert.Single(harness.History.Requests);
        Assert.Equal(new SessionHistoryProject(beta.Id, "beta", beta.Path), request.Project);

        // Вкладка без сессии, мёртвая и с завершённой сессией транскрипт не держат.
        Assert.Equal(["a-1", "b-1"], request.OpenSessionIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Choosing_a_session_open_in_a_tab_switches_to_it_without_a_new_launch()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = await StartedAsync(alpha, beta);
        var open = await OpenWithSessionAsync(harness, 0, "a-1");
        await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        var launches = harness.Workspace.Launches.Count;
        harness.History.Choice = new SessionHistoryChoice(alpha.Id, "a-1");

        var result = await harness.Shell.ShowHistoryAsync(harness.Row(1), CancellationToken.None);

        Assert.Same(open, result);
        Assert.Equal(launches, harness.Workspace.Launches.Count);
        Assert.Equal(2, harness.Shell.Tabs.AllTabs.Count);
        Assert.Same(open, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.Equal(open.TerminalId, harness.Workspace.Inner.VisibleTerminal);
    }

    [Fact]
    public async Task Choosing_a_session_not_open_resumes_it_in_the_row_project()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = await StartedAsync(alpha, beta);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.History.Choice = new SessionHistoryChoice(beta.Id, "b-7");

        // Окно открыто со строки beta и отдаёт сессию её проекта: новая вкладка с --resume
        // в beta становится активной, хотя до этого активной была вкладка alpha.
        var tab = await harness.Shell.ShowHistoryAsync(harness.Row(1), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.Equal(2, harness.Workspace.Launches.Count);
        var launch = harness.Workspace.Launches[^1];
        Assert.Equal(beta.Id, launch.ProjectId);
        Assert.Equal(new SessionLaunch.ResumeSession("b-7"), launch.Launch);
        Assert.Equal("b-7", tab!.SessionId);
        Assert.Equal(beta.Id, tab.ProjectId);
        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
    }

    [Fact]
    public async Task Session_whose_tab_died_is_resumed_in_a_new_tab()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var dead = await OpenWithSessionAsync(harness, 0, "a-1");
        dead.MarkExited(0);
        harness.History.Choice = new SessionHistoryChoice(alpha.Id, "a-1");

        var tab = await harness.Shell.ShowHistoryAsync(harness.Row(0), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.NotSame(dead, tab);
        Assert.Equal(new SessionLaunch.ResumeSession("a-1"), harness.Workspace.Launches[^1].Launch);
    }

    [Fact]
    public async Task Closing_the_history_without_a_choice_changes_nothing()
    {
        var harness = await StartedAsync(Project("alpha", 0), Project("beta", 1));
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var active = harness.Shell.Tabs.ActiveTab;

        var tab = await harness.Shell.ShowHistoryAsync(harness.Row(1), CancellationToken.None);

        Assert.Null(tab);
        Assert.Single(harness.History.Requests);
        Assert.Single(harness.Workspace.Launches);
        Assert.Same(active, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Choice_in_a_project_removed_meanwhile_is_reported_and_launches_nothing()
    {
        var harness = await StartedAsync(Project("alpha", 0));
        harness.History.Choice = new SessionHistoryChoice(Guid.NewGuid(), "gone-1");

        var tab = await harness.Shell.ShowHistoryAsync(harness.Row(0), CancellationToken.None);

        Assert.Null(tab);
        Assert.Empty(harness.Workspace.Launches);
        Assert.Single(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Resume_into_an_unavailable_directory_is_blocked_with_a_message()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        harness.Probe.Remove(alpha.Path);
        harness.History.Choice = new SessionHistoryChoice(alpha.Id, "a-1");

        var tab = await harness.Shell.ShowHistoryAsync(harness.Row(0), CancellationToken.None);

        Assert.Null(tab);
        Assert.Empty(harness.Workspace.Launches);
        Assert.Single(harness.Prompt.Errors);
    }

    [Fact]
    public async Task History_button_is_disabled_on_an_unavailable_row_like_plus()
    {
        var alpha = Project("alpha", 0);
        var harness = new Harness();
        harness.Store.Seed(alpha);
        await harness.Shell.InitializeAsync(CancellationToken.None);
        var row = harness.Row(0);
        Assert.False(row.IsAvailable);

        Assert.False(harness.Shell.ShowHistoryCommand.CanExecute(row));
        Assert.Equal(harness.Shell.OpenSessionCommand.CanExecute(row), harness.Shell.ShowHistoryCommand.CanExecute(row));
    }

    [Fact]
    public async Task History_button_opens_the_window_for_its_row()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);

        Assert.True(harness.Shell.ShowHistoryCommand.CanExecute(harness.Row(0)));
        harness.Shell.ShowHistoryCommand.Execute(harness.Row(0));

        var request = await harness.History.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(alpha.Id, request.Project.Id);
    }

    [Fact]
    public async Task Continue_last_opens_a_tab_with_continue_in_the_row_project()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = await StartedAsync(alpha, beta);

        var tab = await harness.Shell.ContinueLastSessionAsync(harness.Row(1), CancellationToken.None);

        Assert.NotNull(tab);
        var launch = Assert.Single(harness.Workspace.Launches);
        Assert.Equal(beta.Id, launch.ProjectId);
        Assert.IsType<SessionLaunch.ContinueLast>(launch.Launch);
        Assert.Null(tab!.SessionId);
        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
    }

    private static SessionSummary Summary(string sessionId, DateTimeOffset modifiedUtc) =>
        new(sessionId, $@"C:\transcripts\{sessionId}.jsonl", modifiedUtc, 0, "задача", null, null);

    [Fact]
    public async Task Continue_last_switches_to_the_tab_where_the_latest_session_is_live()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = await StartedAsync(alpha, beta);
        var open = await OpenWithSessionAsync(harness, 0, "a-new");
        await OpenWithSessionAsync(harness, 1, "b-1");
        var launches = harness.Workspace.Launches.Count;
        var now = DateTimeOffset.UtcNow;
        harness.HistoryReader.Set(alpha.Path, Summary("a-new", now), Summary("a-old", now.AddHours(-1)));

        var tab = await harness.Shell.ContinueLastSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.Same(open, tab);
        Assert.Equal(launches, harness.Workspace.Launches.Count);
        Assert.Same(open, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.Equal(open.TerminalId, harness.Workspace.Inner.VisibleTerminal);
    }

    [Fact]
    public async Task Continue_last_launches_continue_when_the_latest_session_is_not_live()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);

        // Живая вкладка держит старую сессию, а мёртвая — самую свежую: ни на одну не переключаемся.
        var older = await OpenWithSessionAsync(harness, 0, "a-old");
        var dead = await OpenWithSessionAsync(harness, 0, "a-new");
        dead.MarkExited(0);
        var launches = harness.Workspace.Launches.Count;
        var now = DateTimeOffset.UtcNow;
        harness.HistoryReader.Set(alpha.Path, Summary("a-new", now), Summary("a-old", now.AddHours(-1)));

        var tab = await harness.Shell.ContinueLastSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.NotSame(older, tab);
        Assert.NotSame(dead, tab);
        Assert.Equal(launches + 1, harness.Workspace.Launches.Count);
        Assert.IsType<SessionLaunch.ContinueLast>(harness.Workspace.Launches[^1].Launch);
    }

    [Fact]
    public async Task Continue_last_launches_continue_when_the_project_has_no_history()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        await OpenWithSessionAsync(harness, 0, "a-1");
        var launches = harness.Workspace.Launches.Count;

        var tab = await harness.Shell.ContinueLastSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.Equal(launches + 1, harness.Workspace.Launches.Count);
        Assert.IsType<SessionLaunch.ContinueLast>(harness.Workspace.Launches[^1].Launch);
    }

    [Fact]
    public async Task Continue_last_launches_continue_when_history_cannot_be_read()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        harness.HistoryReader.Fail(alpha.Path);

        var tab = await harness.Shell.ContinueLastSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.IsType<SessionLaunch.ContinueLast>(Assert.Single(harness.Workspace.Launches).Launch);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Continue_last_command_takes_the_row_from_the_parameter()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);

        harness.Shell.ContinueLastSessionCommand.Execute(harness.Row(0));

        // Команда асинхронная, но все подделки завершаются синхронно.
        var launch = Assert.Single(harness.Workspace.Launches);
        Assert.IsType<SessionLaunch.ContinueLast>(launch.Launch);
    }
}

/// <summary>Окно истории: запоминает запросы и возвращает заданный выбор.</summary>
internal sealed class FakeSessionHistoryDialog : ISessionHistoryDialog
{
    /// <summary>Что вернёт окно; <c>null</c> — закрыли без выбора.</summary>
    public SessionHistoryChoice? Choice { get; set; }

    public List<SessionHistoryRequest> Requests { get; } = [];

    /// <summary>Завершается первым запросом — для проверок через команду.</summary>
    public TaskCompletionSource<SessionHistoryRequest> FirstRequest { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SessionHistoryChoice?> ShowAsync(SessionHistoryRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        FirstRequest.TrySetResult(request);
        return Task.FromResult(Choice);
    }
}
