using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Раскладка окна на уровне корневой ViewModel (issue #4): восстановление при старте,
/// запись только живых вкладок и две ловушки затирания — при восстановлении и при закрытии.
/// </summary>
public sealed class LayoutRestoreTests
{
    private static readonly TimeSpan Delay = LayoutRecorder.DebounceDelay;

    private static ProjectDefinition Project(string name, int order) =>
        new(Guid.NewGuid(), name, @"D:\src\" + name, ShellKind.Pwsh, PreLaunch: null, ExtraArgs: [], Order: order);

    private sealed class Harness
    {
        public Harness(ProjectDefinition[] projects, ProjectDefinition[]? unavailable = null)
        {
            Store.Seed(projects);
            foreach (var project in projects)
            {
                if (unavailable is null || !unavailable.Contains(project))
                {
                    Probe.Add(project.Path);
                }
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
                Workspace, list, Prompt, new InlineUiDispatcher(), sessionState, Layouts, Layouts.CreateRecorder(Time),
                Diff.Coordinator, Diff.Tracker);
        }

        public FakeProjectStore Store { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public ShellDiffParts Diff { get; } = new();

        public LaunchRecordingWorkspace Workspace { get; } = new();

        public FakeLayoutStore Layouts { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public ShellViewModel Shell { get; }

        public ITabStateSink Sink => Shell;

        public Task InitializeAsync() => Shell.InitializeAsync(CancellationToken.None);

        /// <summary>Отстаивает окно тишины — отложенная запись уходит в хранилище.</summary>
        public WorkspaceLayout Settle()
        {
            Time.Advance(Delay);
            return Layouts.LastSaved ?? throw new InvalidOperationException("Раскладка не записана.");
        }
    }

    private static async Task<Harness> StartedAsync(params ProjectDefinition[] projects)
    {
        var harness = new Harness(projects);
        await harness.InitializeAsync();
        return harness;
    }

    [Fact]
    public async Task Вкладки_поднимаются_в_сохранённом_порядке_с_resume_и_новой_сессией()
    {
        var alpha = Project("alpha", 0);
        var beta = Project("beta", 1);
        var harness = new Harness([alpha, beta]);
        harness.Layouts.Stored = new WorkspaceLayout(
            alpha.Id,
            [
                new ProjectLayout(beta.Id, 0, [new TabLayout("b-1", "бета")]),
                new ProjectLayout(alpha.Id, 2, [new TabLayout("a-1", "первая"), new TabLayout(null, null), new TabLayout("a-3", "третья")]),
            ]);

        await harness.InitializeAsync();

        Assert.Equal(
            new SessionLaunch[]
            {
                new SessionLaunch.ResumeSession("b-1"),
                new SessionLaunch.ResumeSession("a-1"),
                new SessionLaunch.NewSession(),
                new SessionLaunch.ResumeSession("a-3"),
            },
            harness.Workspace.Launches.Select(static launch => launch.Launch));
        Assert.Equal(
            new[] { beta.Id, alpha.Id, alpha.Id, alpha.Id },
            harness.Workspace.Launches.Select(static launch => launch.ProjectId));

        var tabs = harness.Shell.Tabs;
        Assert.Equal(new[] { "b-1", "a-1", null, "a-3" }, tabs.AllTabs.Select(static tab => tab.SessionId));

        // Имя из раскладки выставлено сразу; вкладке без имени остаётся «новая сессия».
        Assert.Equal(
            new[] { "бета", "первая", TabViewModel.NewSessionTitle, "третья" },
            tabs.AllTabs.Select(static tab => tab.ShortTitle));

        // Активный проект и его активная вкладка — как сохранено, и страница показывает её.
        Assert.Equal(alpha.Id, harness.Shell.ActiveProjectRow?.Id);
        Assert.Same(tabs.AllTabs[3], tabs.ActiveTab);
        Assert.Equal(tabs.AllTabs[3].TerminalId, harness.Workspace.Inner.VisibleTerminal);
        Assert.Same(tabs.AllTabs[0], tabs.ActiveTabFor(beta.Id));
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Вкладки_удалённого_и_недоступного_проектов_пропускаются_молча()
    {
        var alpha = Project("alpha", 0);
        var gone = Project("gone", 1);
        var offline = Project("offline", 2);
        var harness = new Harness([alpha, offline], unavailable: [offline]);
        harness.Layouts.Stored = new WorkspaceLayout(
            gone.Id,
            [
                new ProjectLayout(gone.Id, 0, [new TabLayout("g-1", null)]),
                new ProjectLayout(offline.Id, 0, [new TabLayout("o-1", null)]),
                new ProjectLayout(alpha.Id, 0, [new TabLayout("a-1", null)]),
            ]);

        await harness.InitializeAsync();

        var tab = Assert.Single(harness.Shell.Tabs.AllTabs);
        Assert.Equal("a-1", tab.SessionId);
        Assert.Empty(harness.Prompt.Errors);

        // Активного проекта больше нет — выбран первый поднятый.
        Assert.Equal(alpha.Id, harness.Shell.ActiveProjectRow?.Id);
        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Пока_идёт_восстановление_запись_подавлена_и_sessionId_не_теряется()
    {
        var alpha = Project("alpha", 0);
        var harness = new Harness([alpha]);
        harness.Layouts.Stored = new WorkspaceLayout(
            alpha.Id,
            [new ProjectLayout(alpha.Id, 1, [new TabLayout("a-1", "имя"), new TabLayout("a-2", null), new TabLayout("a-3", null)])]);

        // Между запусками вкладок проходит больше окна тишины: включись запись раньше конца
        // восстановления, на диск легла бы частично поднятая раскладка.
        harness.Workspace.OnOpen = () => harness.Time.Advance(Delay * 2);

        await harness.InitializeAsync();
        harness.Workspace.OnOpen = null;

        Assert.Empty(harness.Layouts.Saves);

        // SessionStart ещё не пришёл, а раскладка уже знает сессии — из режима запуска.
        var saved = harness.Settle();
        Assert.Single(harness.Layouts.Saves);
        var project = Assert.Single(saved.Projects);
        Assert.Equal(
            new[] { new TabLayout("a-1", "имя"), new TabLayout("a-2", null), new TabLayout("a-3", null) },
            project.Tabs);
        Assert.Equal(1, project.ActiveTabIndex);
        Assert.Equal(alpha.Id, saved.ActiveProjectId);
    }

    [Fact]
    public async Task Вкладка_после_SessionEnd_и_вкладка_с_умершей_оболочкой_в_раскладку_не_попадают()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var row = harness.Shell.Projects.Rows[0];
        var ended = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var exited = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var live = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        harness.Sink.SetSessionContext(ended!.TerminalId, "s-ended", null, false);
        harness.Sink.SetSessionContext(exited!.TerminalId, "s-exited", null, false);
        harness.Sink.SetSessionContext(live!.TerminalId, "s-live", null, false);

        harness.Sink.SetSessionContext(ended.TerminalId, "s-ended", null, true);
        harness.Workspace.Inner.RaiseExited(exited.TerminalId, 0);

        var saved = harness.Settle();
        var tab = Assert.Single(Assert.Single(saved.Projects).Tabs);
        Assert.Equal("s-live", tab.SessionId);
    }

    [Fact]
    public async Task После_clear_раскладка_хранит_новую_сессию()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var tab = await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);
        harness.Sink.SetSessionContext(tab!.TerminalId, "old", null, false);
        harness.Settle();

        // /clear: SessionEnd старой сессии и сразу SessionStart новой — внутри окна тишины.
        harness.Sink.SetSessionContext(tab.TerminalId, "old", null, true);
        harness.Time.Advance(Delay / 4);
        harness.Sink.SetSessionContext(tab.TerminalId, "new", null, false);

        var saved = harness.Settle();
        Assert.Equal("new", Assert.Single(Assert.Single(saved.Projects).Tabs).SessionId);
    }

    [Fact]
    public async Task Хук_с_теми_же_значениями_запись_не_взводит()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var tab = await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);
        harness.Sink.SetSessionContext(tab!.TerminalId, "s", @"D:\src\alpha", false);
        harness.Settle();
        var saves = harness.Layouts.Saves.Count;

        // Stop, PreToolUse и прочие хуки главного агента несут те же session_id и cwd.
        for (var i = 0; i < 10; i++)
        {
            harness.Sink.SetSessionContext(tab.TerminalId, "s", @"D:\src\alpha\sub", null);
        }

        harness.Time.Advance(Delay * 4);
        Assert.Equal(saves, harness.Layouts.Saves.Count);
    }

    [Fact]
    public async Task Перестановка_и_смена_активной_вкладки_попадают_в_раскладку()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var row = harness.Shell.Projects.Rows[0];
        var first = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        harness.Sink.SetSessionContext(first!.TerminalId, "one", null, false);
        harness.Sink.SetSessionContext(second!.TerminalId, "two", null, false);
        harness.Settle();

        harness.Shell.Tabs.Reorder(second, 0);
        await harness.Shell.ActivateTabAsync(first, CancellationToken.None);

        var project = Assert.Single(harness.Settle().Projects);
        Assert.Equal(new[] { "two", "one" }, project.Tabs.Select(static tab => tab.SessionId));
        Assert.Equal(1, project.ActiveTabIndex);
    }

    [Fact]
    public async Task Закрытие_окна_записывает_раскладку_до_гашения_и_гашение_её_не_затирает()
    {
        var alpha = Project("alpha", 0);
        var harness = await StartedAsync(alpha);
        var row = harness.Shell.Projects.Rows[0];
        var first = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        harness.Sink.SetSessionContext(first!.TerminalId, "one", null, false);
        harness.Sink.SetSessionContext(second!.TerminalId, "two", null, false);

        // Окно закрывают раньше, чем истекло окно тишины.
        await harness.Shell.PersistLayoutAndFreezeAsync(CancellationToken.None);
        var saves = harness.Layouts.Saves.Count;

        // Гашение: SessionEnd и выход оболочек у всех вкладок, затем освобождение.
        harness.Sink.SetSessionContext(first.TerminalId, "one", null, true);
        harness.Sink.SetSessionContext(second.TerminalId, "two", null, true);
        harness.Workspace.Inner.RaiseExited(first.TerminalId, 0);
        harness.Workspace.Inner.RaiseExited(second.TerminalId, 0);
        await harness.Shell.DisposeAsync();
        harness.Time.Advance(Delay * 4);

        Assert.Equal(saves, harness.Layouts.Saves.Count);
        var project = Assert.Single(harness.Layouts.LastSaved!.Projects);
        Assert.Equal(new[] { "one", "two" }, project.Tabs.Select(static tab => tab.SessionId));
    }

    private static async Task<(Harness Harness, ProjectDefinition Alpha, ProjectDefinition Offline, ProjectLayout Saved)> WithOfflineProjectAsync()
    {
        var alpha = Project("alpha", 0);
        var gone = Project("gone", 1);
        var offline = Project("offline", 2);
        var saved = new ProjectLayout(offline.Id, 1, [new TabLayout("o-1", "один"), new TabLayout("o-2", null)]);
        var harness = new Harness([alpha, offline], unavailable: [offline]);
        harness.Layouts.Stored = new WorkspaceLayout(
            alpha.Id,
            [
                new ProjectLayout(gone.Id, 0, [new TabLayout("g-1", null)]),
                saved,
                new ProjectLayout(alpha.Id, 0, [new TabLayout("a-1", null)]),
            ]);

        await harness.InitializeAsync();
        return (harness, alpha, offline, saved);
    }

    [Fact]
    public async Task Записи_проекта_с_недоступным_каталогом_переносятся_а_удалённого_отбрасываются()
    {
        var (harness, alpha, offline, saved) = await WithOfflineProjectAsync();

        Assert.DoesNotContain(harness.Workspace.Launches, launch => launch.ProjectId == offline.Id);

        var layout = harness.Settle();
        Assert.Equal(new[] { alpha.Id, offline.Id }, layout.Projects.Select(static project => project.ProjectId));
        Assert.Same(saved, layout.Projects[1]);
    }

    [Fact]
    public async Task Живая_вкладка_в_вернувшемся_каталоге_заменяет_перенесённые_записи()
    {
        var (harness, _, offline, _) = await WithOfflineProjectAsync();
        harness.Probe.Add(offline.Path);
        var row = harness.Shell.Projects.Rows.Single(candidate => candidate.Id == offline.Id);

        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        harness.Sink.SetSessionContext(tab!.TerminalId, "fresh", null, false);

        var project = harness.Settle().Projects.Single(candidate => candidate.ProjectId == offline.Id);
        Assert.Equal("fresh", Assert.Single(project.Tabs).SessionId);

        // И после закрытия живой вкладки прежние записи не возвращаются.
        await harness.Shell.CloseTabAsync(tab, CancellationToken.None);
        Assert.DoesNotContain(harness.Settle().Projects, candidate => candidate.ProjectId == offline.Id);
    }

    [Fact]
    public async Task Убранный_из_списка_проект_уносит_перенесённые_записи()
    {
        var (harness, alpha, offline, _) = await WithOfflineProjectAsync();
        var row = harness.Shell.Projects.Rows.Single(candidate => candidate.Id == offline.Id);

        Assert.True(await harness.Shell.RemoveProjectAsync(row, CancellationToken.None));

        Assert.Equal(new[] { alpha.Id }, harness.Settle().Projects.Select(static project => project.ProjectId));
    }
}
