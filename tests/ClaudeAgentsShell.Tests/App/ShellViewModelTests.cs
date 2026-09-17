using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Поведение корневой ViewModel: панель проектов, вкладки и горячие клавиши.
/// Проверяется состояние после действия, а не факт вызова портов.
/// </summary>
public sealed class ShellViewModelTests
{
    private const string PathA = @"D:\src\alpha";
    private const string PathB = @"D:\src\beta";

    private static ProjectDefinition Project(string name, string path, int order) =>
        new(Guid.NewGuid(), name, path, ShellKind.Pwsh, PreLaunch: null, ExtraArgs: [], Order: order);

    private sealed class Harness
    {
        public Harness(params ProjectDefinition[] projects)
        {
            Store.Seed(projects);
            foreach (var project in projects)
            {
                Probe.Add(project.Path);
            }

            var list = new ProjectListViewModel(Store, BranchReader, Watcher, Probe, Picker, new InlineUiDispatcher());
            Shell = new ShellViewModel(Workspace, list, Prompt, new InlineUiDispatcher());
        }

        public FakeProjectStore Store { get; } = new();

        public FakeGitBranchReader BranchReader { get; } = new();

        public FakeGitBranchWatcher Watcher { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeFolderPicker Picker { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public FakeTerminalWorkspace Workspace { get; } = new();

        public ShellViewModel Shell { get; }

        public ProjectRowViewModel Row(int index) => Shell.Projects.Rows[index];

        public Task InitializeAsync() => Shell.InitializeAsync(CancellationToken.None);
    }

    private static async Task<Harness> StartedAsync(params ProjectDefinition[] projects)
    {
        var harness = new Harness(projects);
        await harness.InitializeAsync();
        return harness;
    }

    [Fact]
    public async Task Clicking_a_project_without_open_tabs_does_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.Null(harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public async Task Plus_on_a_project_row_opens_a_tab_and_makes_it_active()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.NotNull(tab);
        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
        Assert.True(tab!.IsActive);
        Assert.Equal(tab.TerminalId, harness.Workspace.VisibleTerminal);
        Assert.Equal(PathA, Assert.Single(harness.Workspace.OpenedDirectories));
        Assert.Equal(TabViewModel.NewSessionTitle, tab.ShortTitle);
        Assert.Equal("alpha · новая сессия", tab.Title);
    }

    [Fact]
    public async Task Closing_the_last_tab_of_a_project_returns_the_row_to_a_dash()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        Assert.Equal("1", row.SessionCountText);

        await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        Assert.Equal("—", row.SessionCountText);
        Assert.False(row.HasSessions);
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Session_count_is_per_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        Assert.Equal("2", harness.Row(0).SessionCountText);
        Assert.Equal("1", harness.Row(1).SessionCountText);

        await harness.Shell.CloseTabAsync(betaTab!, CancellationToken.None);

        Assert.Equal("2", harness.Row(0).SessionCountText);
        Assert.Equal("—", harness.Row(1).SessionCountText);
    }

    [Fact]
    public async Task Missing_directory_blocks_the_launch()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        Assert.True(row.IsAvailable);

        // Каталог исчез уже после загрузки списка.
        harness.Probe.Remove(PathA);

        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        Assert.Null(tab);
        Assert.False(row.IsAvailable);
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Empty(harness.Workspace.OpenedDirectories);
        Assert.Single(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Missing_directory_at_startup_leaves_the_row_unavailable_and_unwatched()
    {
        var project = Project("alpha", PathA, 0);
        var harness = new Harness();
        harness.Store.Seed(project);

        await harness.InitializeAsync();

        Assert.False(harness.Row(0).IsAvailable);
        Assert.Null(harness.Row(0).Branch);
        Assert.Empty(harness.Watcher.Watched);
    }

    [Fact]
    public async Task Failed_launch_leaves_the_window_alive_and_shows_the_error()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        harness.Workspace.OpenFailure = new ShellNotFoundException("нет оболочки");

        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.Null(tab);
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Equal("нет оболочки", Assert.Single(harness.Prompt.Errors));
    }

    [Fact]
    public async Task Clicking_a_project_row_switches_to_its_last_active_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.ActivateTabAsync(first!, CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        Assert.Same(betaTab, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Same(first, harness.Shell.Tabs.ActiveTab);
        Assert.NotSame(second, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(first!.TerminalId, harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public async Task Closing_a_live_tab_asks_for_confirmation_and_a_refusal_keeps_it()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Prompt.ConfirmResult = false;

        var closed = await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        Assert.False(closed);
        Assert.Single(harness.Prompt.Confirmations);
        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Empty(harness.Workspace.Closed);
        Assert.Equal("1", harness.Row(0).SessionCountText);
    }

    [Fact]
    public async Task Closing_a_tab_whose_process_is_gone_asks_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        harness.Workspace.RaiseExited(tab!.TerminalId, 0);
        Assert.False(tab.IsRunning);

        var closed = await harness.Shell.CloseTabAsync(tab, CancellationToken.None);

        Assert.True(closed);
        Assert.Empty(harness.Prompt.Confirmations);
        Assert.Empty(harness.Shell.Tabs.Tabs);
    }

    [Fact]
    public async Task An_exited_process_does_not_close_the_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        harness.Workspace.RaiseExited(tab!.TerminalId, 1);

        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
        Assert.Equal("1", harness.Row(0).SessionCountText);
    }

    [Fact]
    public async Task Closing_the_active_tab_activates_the_neighbour()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.CloseTabAsync(second!, CancellationToken.None);

        Assert.Same(first, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(first!.TerminalId, harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public async Task Ctrl_digit_switches_by_number_and_ignores_numbers_beyond_the_strip()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 1, CancellationToken.None);
        Assert.Same(first, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 2, CancellationToken.None);
        Assert.Same(second, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 9, CancellationToken.None);
        Assert.Same(second, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Ctrl_tab_walks_the_strip_in_a_circle()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var third = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        Assert.Same(third, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        Assert.Same(first, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.PreviousTab, 0, CancellationToken.None);
        Assert.Same(third, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.PreviousTab, 0, CancellationToken.None);
        Assert.Same(second, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Ctrl_tab_with_a_single_tab_keeps_it_active()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var only = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.PreviousTab, 0, CancellationToken.None);

        Assert.Same(only, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Shortcuts_without_tabs_and_without_a_chosen_project_do_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 1, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.CloseTab, 0, CancellationToken.None);
        // Проект не выбран: открывать сессию не в чем, но и ошибки это не повод показывать.
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NewSession, 0, CancellationToken.None);

        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Empty(harness.Prompt.Confirmations);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Clicking_a_project_row_makes_it_the_target_for_a_new_session()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        Assert.Null(harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.NewSessionCommand.CanExecute(null));

        // Клик по строке без вкладок переключать нечего, но проект он выбирает.
        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);

        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
        Assert.True(harness.Row(1).IsCurrent);
        Assert.True(harness.Shell.NewSessionCommand.CanExecute(null));

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NewSession, 0, CancellationToken.None);

        Assert.Single(harness.Shell.Tabs.Tabs);
        Assert.Equal(PathB, Assert.Single(harness.Workspace.OpenedDirectories));
    }

    [Fact]
    public async Task A_chosen_project_with_a_missing_directory_does_not_offer_a_new_session()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        Assert.True(harness.Shell.NewSessionCommand.CanExecute(null));

        harness.Probe.Remove(PathA);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.False(harness.Row(0).IsAvailable);
        Assert.False(harness.Shell.NewSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_last_closed_tab_leaves_its_project_chosen()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        // Полоса пуста, но Ctrl+Shift+T по-прежнему знает, куда открывать.
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NewSession, 0, CancellationToken.None);

        Assert.Single(harness.Shell.Tabs.Tabs);
        Assert.Equal([PathB, PathB], harness.Workspace.OpenedDirectories);
    }

    [Fact]
    public async Task Opening_a_session_moves_the_choice_to_that_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

        // Выбрали beta, но запустили сессию кнопкой на строке alpha.
        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        // Выбранным остаётся тот проект, с которым работали последним.
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.True(harness.Row(0).IsCurrent);
        Assert.False(harness.Row(1).IsCurrent);
    }

    [Fact]
    public async Task A_tab_whose_pty_failed_to_start_closes_without_a_question()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        // Псевдоконсоль поднимается после возврата из OpenAsync; её сбой приходит
        // событием с ненулевым кодом, и вкладка перестаёт считаться живой.
        harness.Workspace.RaiseExited(tab!.TerminalId, 1);

        Assert.False(tab.IsRunning);
        Assert.True(await harness.Shell.CloseTabAsync(tab, CancellationToken.None));
        Assert.Empty(harness.Prompt.Confirmations);
    }

    [Fact]
    public async Task A_failed_save_adds_no_row()
    {
        var harness = await StartedAsync();
        harness.Picker.NextFolder = @"D:\src\gamma";
        harness.Probe.Add(@"D:\src\gamma");
        harness.Store.SaveFailure = new IOException("диск занят");

        await Assert.ThrowsAsync<IOException>(() => harness.Shell.AddProjectAsync(CancellationToken.None));

        Assert.Empty(harness.Shell.Projects.Rows);
    }

    [Fact]
    public async Task The_same_folder_is_not_added_twice()
    {
        var harness = await StartedAsync();
        harness.Picker.NextFolder = @"D:\src\gamma";
        harness.Probe.Add(@"D:\src\gamma");

        await harness.Shell.AddProjectAsync(CancellationToken.None);
        harness.Picker.NextFolder = @"D:\src\gamma";
        await harness.Shell.AddProjectAsync(CancellationToken.None);

        Assert.Single(harness.Shell.Projects.Rows);
        Assert.Equal(1, harness.Store.SaveCount);
    }

    [Fact]
    public async Task Reloading_the_list_drops_the_previous_watchers()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        Assert.Equal([PathA], harness.Watcher.Watched);

        harness.Store.Seed(Project("beta", PathB, 0));
        harness.Probe.Add(PathB);
        await harness.Shell.Projects.LoadAsync(CancellationToken.None);

        Assert.Equal([PathB], harness.Watcher.Watched);
    }

    [Fact]
    public async Task New_session_shortcut_opens_a_tab_in_the_project_of_the_active_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NewSession, 0, CancellationToken.None);

        Assert.Equal(2, harness.Shell.Tabs.Tabs.Count);
        Assert.Equal("2", harness.Row(1).SessionCountText);
        Assert.Equal("—", harness.Row(0).SessionCountText);
        Assert.Equal([PathB, PathB], harness.Workspace.OpenedDirectories);
    }

    [Fact]
    public async Task Close_shortcut_closes_the_active_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.CloseTab, 0, CancellationToken.None);

        Assert.Same(first, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Equal(second!.TerminalId, Assert.Single(harness.Workspace.Closed));
    }

    [Fact]
    public async Task Adding_a_project_names_it_after_the_folder_and_stores_it()
    {
        var harness = await StartedAsync();
        harness.Picker.NextFolder = @"D:\src\gamma";
        harness.Probe.Add(@"D:\src\gamma");
        harness.BranchReader.Set(@"D:\src\gamma", "main");

        await harness.Shell.AddProjectAsync(CancellationToken.None);

        var row = Assert.Single(harness.Shell.Projects.Rows);
        Assert.Equal("gamma", row.Name);
        Assert.Equal(@"D:\src\gamma", row.Path);
        Assert.Equal("main", row.Branch);
        Assert.Equal(@"D:\src\gamma · main", row.PathAndBranch);
        Assert.Equal("—", row.SessionCountText);
        Assert.Equal("gamma", Assert.Single(harness.Store.Saved).Name);
    }

    [Fact]
    public async Task Cancelled_folder_dialog_adds_nothing()
    {
        var harness = await StartedAsync();
        harness.Picker.NextFolder = null;

        await harness.Shell.AddProjectAsync(CancellationToken.None);

        Assert.Empty(harness.Shell.Projects.Rows);
        Assert.Equal(0, harness.Store.SaveCount);
    }

    [Fact]
    public async Task Branch_change_reaches_the_row()
    {
        var project = Project("alpha", PathA, 0);
        var harness = new Harness(project);
        harness.BranchReader.Set(PathA, "main");
        await harness.InitializeAsync();
        Assert.Equal("main", harness.Row(0).Branch);

        harness.Watcher.Raise(PathA + @"\", "feat/sensor-repo");

        Assert.Equal("feat/sensor-repo", harness.Row(0).Branch);
        Assert.Equal(PathA + " · feat/sensor-repo", harness.Row(0).PathAndBranch);
    }

    [Fact]
    public async Task Disposed_shell_stops_listening_to_the_workspace()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.DisposeAsync();
        await harness.Shell.DisposeAsync();
        harness.Workspace.RaiseExited(tab!.TerminalId, 0);

        Assert.True(harness.Workspace.Disposed);
        Assert.True(tab.IsRunning);
    }

    [Fact]
    public async Task Active_project_row_follows_the_active_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        Assert.True(harness.Row(0).IsCurrent);
        Assert.False(harness.Row(1).IsCurrent);

        await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        Assert.False(harness.Row(0).IsCurrent);
        Assert.True(harness.Row(1).IsCurrent);

        await harness.Shell.ActivateTabAsync(alphaTab!, CancellationToken.None);
        Assert.True(harness.Row(0).IsCurrent);
    }

    [Fact]
    public async Task Closing_an_already_closed_tab_changes_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.True(await harness.Shell.CloseTabAsync(tab!, CancellationToken.None));
        // Повторный вызов приходит от удержанного Ctrl+Shift+W, пока висело подтверждение.
        Assert.False(await harness.Shell.CloseTabAsync(tab!, CancellationToken.None));

        Assert.Single(harness.Workspace.Closed);
        Assert.Single(harness.Prompt.Confirmations);
        Assert.Empty(harness.Shell.Tabs.Tabs);
    }

    [Fact]
    public async Task Exit_of_an_unknown_terminal_is_tolerated()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        // Пользователь закрыл вкладку, а событие выхода оболочки уже было в пути.
        harness.Workspace.RaiseExited(new TerminalId("призрак"), 1);

        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.True(tab!.IsRunning);
    }

    [Fact]
    public async Task Awaiting_input_counter_stays_empty_without_hooks()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        // Состояние приходит от хуков (M4); до тех пор все вкладки в TabState.Unknown.
        Assert.All(harness.Shell.Tabs.Tabs, tab => Assert.Equal(TabState.Unknown, tab.State));
        Assert.Equal(0, harness.Shell.Tabs.AwaitingInputCount);
        Assert.False(harness.Shell.Tabs.HasAwaitingInput);
    }
}
