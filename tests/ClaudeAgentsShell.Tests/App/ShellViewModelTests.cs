using System.ComponentModel;
using ClaudeAgentsShell.App.Diff;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.State;
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

            var list = new ProjectListViewModel(
                Store, BranchReader, Watcher, Probe, Picker, Dialog, Prompt, Launcher, new InlineUiDispatcher());
            var sessionState = new SessionStateCoordinator(Hooks, Workspace, History, new InlineUiDispatcher());
            var layouts = new FakeLayoutStore();
            Shell = new ShellViewModel(
                Workspace, list, Prompt, new InlineUiDispatcher(), sessionState, layouts.CreateService(),
                Diff.Coordinator, Diff.Tracker);
        }

        public FakeProjectStore Store { get; } = new();

        public FakeHookListener Hooks { get; } = new();

        public FakeSessionHistoryReader History { get; } = new();

        public FakeGitBranchReader BranchReader { get; } = new();

        public FakeGitBranchWatcher Watcher { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeFolderPicker Picker { get; } = new();

        public FakeProjectSettingsDialog Dialog { get; } = new();

        public FakeShellLauncher Launcher { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public ShellDiffParts Diff { get; } = new();

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
    public async Task Clicking_a_project_without_open_tabs_selects_it_and_leaves_the_strip_empty()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        // Полоса пуста, но это видимый результат: проект выбран, «+» работает.
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.Null(harness.Workspace.VisibleTerminal);
        Assert.True(harness.Row(0).IsCurrent);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.IsTerminalVisible);
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
        harness.Dialog.Edit = project => project;
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
        harness.Dialog.Edit = project => project;

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
        harness.Dialog.Edit = project => project;

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
    public async Task The_strip_shows_only_the_tabs_of_the_selected_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

        var alphaFirst = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var alphaSecond = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // Последней открыли вкладку beta — полоса показывает только её.
        Assert.Same(betaTab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Equal(3, harness.Shell.Tabs.AllTabs.Count);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Equal([alphaFirst, alphaSecond], harness.Shell.Tabs.Tabs);
        Assert.DoesNotContain(betaTab, harness.Shell.Tabs.Tabs);
        Assert.Equal(3, harness.Shell.Tabs.AllTabs.Count);
    }

    [Fact]
    public async Task Switching_to_a_project_without_tabs_empties_the_strip_and_hides_the_terminal()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        Assert.True(harness.Shell.IsTerminalVisible);

        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);

        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.False(alphaTab!.IsActive);

        // Вкладка alpha жива, её терминал никто не гасил — просто он больше не показан:
        // иначе пользователь видел бы терминал чужого проекта под пустой полосой.
        Assert.Same(alphaTab, Assert.Single(harness.Shell.Tabs.AllTabs));
        Assert.Empty(harness.Workspace.Closed);
        Assert.False(harness.Shell.IsTerminalVisible);
        Assert.Contains("beta", harness.Shell.TerminalPlaceholderText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_projects_closes_no_tabs_and_keeps_their_terminals()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);
        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Empty(harness.Workspace.Closed);
        Assert.Equal([alphaTab!.TerminalId, betaTab!.TerminalId], harness.Workspace.Terminals);
        Assert.True(alphaTab.IsRunning);
        Assert.True(betaTab.IsRunning);
        Assert.Equal("1", harness.Row(0).SessionCountText);
        Assert.Equal("1", harness.Row(1).SessionCountText);
    }

    [Fact]
    public async Task Ctrl_tab_walks_only_the_tabs_of_the_selected_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaFirst = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var alphaSecond = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // В beta открыта одна вкладка: круг из одной вкладки никуда не ведёт.
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        Assert.Same(betaTab, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        Assert.Same(alphaSecond, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        Assert.Same(alphaFirst, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.PreviousTab, 0, CancellationToken.None);
        Assert.Same(alphaSecond, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Ctrl_digit_counts_from_the_first_tab_of_the_selected_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaFirst = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        var betaSecond = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // Ctrl+1 — первая вкладка beta, а не первая из всех открытых.
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 1, CancellationToken.None);
        Assert.Same(betaFirst, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 2, CancellationToken.None);
        Assert.Same(betaSecond, harness.Shell.Tabs.ActiveTab);

        // Третьей вкладки в beta нет, хотя всего открыто четыре.
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 3, CancellationToken.None);
        Assert.Same(betaSecond, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Shortcuts_on_an_empty_strip_do_nothing_and_leave_foreign_tabs_alone()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NextTab, 0, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.PreviousTab, 0, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.SelectTab, 1, CancellationToken.None);
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.CloseTab, 0, CancellationToken.None);

        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Empty(harness.Workspace.Closed);
        Assert.Empty(harness.Prompt.Confirmations);
        Assert.Same(alphaTab, Assert.Single(harness.Shell.Tabs.AllTabs));
    }

    [Fact]
    public async Task A_new_session_on_an_empty_strip_opens_in_the_selected_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.ActivateProjectAsync(harness.Row(1), CancellationToken.None);
        Assert.Empty(harness.Shell.Tabs.Tabs);

        // Пустая полоса — не тупик: «+» и Ctrl+Shift+T работают.
        Assert.True(harness.Shell.NewSessionCommand.CanExecute(null));
        await harness.Shell.ApplyShortcutAsync(ShellShortcut.NewSession, 0, CancellationToken.None);

        var opened = Assert.Single(harness.Shell.Tabs.Tabs);
        Assert.Equal(harness.Row(1).Id, opened.ProjectId);
        Assert.True(harness.Shell.IsTerminalVisible);
        Assert.Equal([PathA, PathB], harness.Workspace.OpenedDirectories);
    }

    [Fact]
    public async Task Opening_a_tab_switches_the_strip_to_its_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        // Кнопка «+» на строке beta при выбранной alpha.
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
        Assert.True(harness.Row(1).IsCurrent);
        Assert.False(harness.Row(0).IsCurrent);
        Assert.Same(betaTab, Assert.Single(harness.Shell.Tabs.Tabs));
    }

    [Fact]
    public async Task Closing_the_last_tab_of_a_project_leaves_it_selected_with_an_empty_strip()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaTab = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.CloseTabAsync(betaTab!, CancellationToken.None);

        // Полоса пуста, но переключения на чужой проект не случилось.
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.IsTerminalVisible);

        // Вкладка alpha осталась открытой и вернётся, когда вернутся к её проекту.
        Assert.Same(alphaTab, Assert.Single(harness.Shell.Tabs.AllTabs));
        Assert.Equal(betaTab!.TerminalId, Assert.Single(harness.Workspace.Closed));

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        Assert.Same(alphaTab, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(alphaTab!.TerminalId, harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public async Task Closing_the_active_tab_activates_a_neighbour_of_the_same_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var betaFirst = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        var betaSecond = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.CloseTabAsync(betaSecond!, CancellationToken.None);

        // Соседняя берётся внутри проекта: вкладка alpha открыта раньше, но она чужая.
        Assert.Same(betaFirst, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(betaFirst!.TerminalId, harness.Workspace.VisibleTerminal);
        Assert.NotSame(alphaTab, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task An_exit_event_reaches_a_tab_of_a_project_that_is_not_selected()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alphaTab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // Оболочка скрытой вкладки вышла: не заметив этого, мы спросили бы подтверждение
        // закрытия у вкладки, под которой давно нет процесса.
        harness.Workspace.RaiseExited(alphaTab!.TerminalId, 1);

        Assert.False(alphaTab.IsRunning);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        Assert.True(await harness.Shell.CloseTabAsync(alphaTab, CancellationToken.None));
        Assert.Empty(harness.Prompt.Confirmations);
    }

    [Fact]
    public async Task The_terminal_area_stays_shown_until_the_page_is_up()
    {
        var harness = new Harness(Project("alpha", PathA, 0));

        // Прятать область терминала до того, как страница поднялась, нельзя: WebView2 —
        // дочерний HWND и создаётся, когда впервые получает место в разметке.
        Assert.True(harness.Shell.IsTerminalVisible);

        await harness.InitializeAsync();

        Assert.False(harness.Shell.IsTerminalVisible);
    }

    [Fact]
    public async Task A_project_without_a_choice_explains_what_to_do()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        Assert.Null(harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.IsTerminalVisible);
        Assert.Contains("Выберите проект", harness.Shell.TerminalPlaceholderText, StringComparison.Ordinal);

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        Assert.Contains("Ctrl+Shift+T", harness.Shell.TerminalPlaceholderText, StringComparison.Ordinal);
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

    [Fact]
    public async Task Awaiting_input_counter_counts_tabs_of_every_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var alpha = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var beta = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        alpha!.State = TabState.AwaitingInput;
        beta!.State = TabState.AwaitingInput;

        // Полоса показывает вкладки одного проекта, а счётчик считает все: смысл счётчика —
        // заметить сессию, которая ждёт в проекте, который сейчас не на экране.
        Assert.Single(harness.Shell.Tabs.Tabs);
        Assert.Equal(2, harness.Shell.Tabs.AwaitingInputCount);
        Assert.True(harness.Shell.Tabs.HasAwaitingInput);
    }

    [Fact]
    public async Task Counter_recalculates_when_a_tab_changes_state()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)harness.Shell.Tabs).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab!.State = TabState.AwaitingInput;

        Assert.Equal(1, harness.Shell.Tabs.AwaitingInputCount);
        Assert.True(harness.Shell.Tabs.HasAwaitingInput);

        // Без этих уведомлений счётчик в разметке застынет на значении, которое
        // вычислилось при открытии вкладки.
        Assert.Contains(nameof(TabStripViewModel.AwaitingInputCount), changed);
        Assert.Contains(nameof(TabStripViewModel.HasAwaitingInput), changed);

        tab.State = TabState.Busy;

        Assert.Equal(0, harness.Shell.Tabs.AwaitingInputCount);
        Assert.False(harness.Shell.Tabs.HasAwaitingInput);
    }

    [Fact]
    public async Task Counter_command_is_disabled_until_a_tab_starts_waiting()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.False(harness.Shell.ShowAwaitingTabCommand.CanExecute(null));

        tab!.State = TabState.AwaitingInput;

        Assert.True(harness.Shell.ShowAwaitingTabCommand.CanExecute(null));
    }

    [Fact]
    public async Task Counter_without_awaiting_tabs_leaves_everything_as_it_was()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.ShowAwaitingTabAsync(CancellationToken.None);

        Assert.Same(tab, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
    }

    [Fact]
    public async Task Clicking_the_counter_switches_both_the_project_and_the_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var waiting = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // Возвращаемся в alpha: ждущая вкладка beta уходит из полосы.
        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);
        waiting!.State = TabState.AwaitingInput;
        Assert.DoesNotContain(waiting, harness.Shell.Tabs.Tabs);

        await harness.Shell.ShowAwaitingTabAsync(CancellationToken.None);

        // Переключиться обязано всё сразу: иначе команда «сработает», а на экране
        // не изменится ничего.
        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
        Assert.Same(waiting, harness.Shell.Tabs.ActiveTab);
        Assert.Contains(waiting, harness.Shell.Tabs.Tabs);
        Assert.Equal(waiting.TerminalId, harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public async Task Counter_leads_to_the_tab_that_was_opened_first_not_the_one_waiting_longest()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        // Ждать начала вторая, но «первая» считается в порядке открытия: так цель клика
        // не зависит от того, в каком порядке пришли события хуков.
        second!.State = TabState.AwaitingInput;
        first!.State = TabState.AwaitingInput;

        await harness.Shell.ShowAwaitingTabAsync(CancellationToken.None);

        Assert.Same(first, harness.Shell.Tabs.ActiveTab);
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
    }

    [Fact]
    public async Task Dead_tab_loses_its_marker_and_leaves_the_awaiting_counter()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        tab!.State = TabState.AwaitingInput;
        Assert.Equal(1, harness.Shell.Tabs.AwaitingInputCount);

        // Процесс оболочки убит извне: SessionEnd не придёт, и ввода у вкладки без помпы
        // уже не будет — маркер обязан сняться здесь, иначе он останется до конца сеанса.
        harness.Workspace.RaiseExited(tab.TerminalId, exitCode: 1);

        Assert.False(tab.IsRunning);
        Assert.Equal(TabState.Unknown, tab.State);
        Assert.Equal(0, harness.Shell.Tabs.AwaitingInputCount);
        Assert.False(harness.Shell.Tabs.HasAwaitingInput);
    }

    [Fact]
    public async Task Context_menu_opens_the_project_folder_in_explorer()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        await harness.Shell.OpenProjectFolderAsync(harness.Row(0), CancellationToken.None);

        Assert.Equal(PathA, Assert.Single(harness.Launcher.Opened));
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Folder_that_does_not_open_reports_an_error_instead_of_throwing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        harness.Launcher.Result = false;

        await harness.Shell.OpenProjectFolderAsync(harness.Row(0), CancellationToken.None);

        Assert.Contains(PathA, Assert.Single(harness.Prompt.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_settings_update_the_row_and_the_stored_list()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        // Диалог, который вдобавок «перепутал» идентификатор и порядок: и то и другое
        // принадлежит списку, а не диалогу, и обязано уцелеть.
        harness.Dialog.Edit = project => project with
        {
            Name = "omega",
            Shell = ShellKind.Cmd,
            Id = Guid.NewGuid(),
            Order = 99,
        };

        await harness.Shell.ShowProjectSettingsAsync(row, CancellationToken.None);

        Assert.Equal("omega", row.Name);
        Assert.Same(row, harness.Row(0));

        var saved = Assert.Single(harness.Store.Saved);
        Assert.Equal("omega", saved.Name);
        Assert.Equal(ShellKind.Cmd, saved.Shell);
        Assert.Equal(row.Id, saved.Id);
        Assert.Equal(0, saved.Order);
    }

    [Fact]
    public async Task Cancelled_settings_dialog_changes_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        // Edit не задан: диалог возвращает null — пользователь закрыл его отказом.
        await harness.Shell.ShowProjectSettingsAsync(row, CancellationToken.None);

        Assert.Single(harness.Dialog.Shown);
        Assert.Equal("alpha", row.Name);
        Assert.Equal(0, harness.Store.SaveCount);
    }

    [Fact]
    public async Task Changed_project_path_moves_the_branch_watcher()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        // Новый каталог существует и стоит на другой ветке.
        harness.Probe.Add(PathB);
        harness.BranchReader.Set(PathB, "release");
        harness.Dialog.Edit = project => project with { Path = PathB };

        await harness.Shell.ShowProjectSettingsAsync(row, CancellationToken.None);

        Assert.Equal(PathB, row.Path);
        Assert.Equal("release", row.Branch);
        Assert.True(row.IsAvailable);
        Assert.Equal(PathB, Assert.Single(harness.Watcher.Watched));
    }

    [Fact]
    public async Task Settings_that_failed_to_save_leave_the_row_untouched()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        harness.Dialog.Edit = project => project with { Name = "omega" };
        harness.Store.SaveFailure = new InvalidOperationException("projects.json занят");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Shell.ShowProjectSettingsAsync(row, CancellationToken.None));

        Assert.Equal("alpha", row.Name);
        Assert.Same(row, harness.Row(0));
    }

    [Fact]
    public async Task Renaming_a_project_renames_the_titles_of_its_open_tabs()
    {
        var harness = await StartedAsync(
            Project("alpha", PathA, 0),
            Project("beta", PathB, 1));
        var alpha = harness.Row(0);

        var first = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);
        var stranger = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        first!.ShortTitle = "почини сборку";

        var titleChanges = 0;
        first.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TabViewModel.Title))
            {
                titleChanges++;
            }
        };

        harness.Dialog.Edit = project => project with { Name = "omega" };
        await harness.Shell.ShowProjectSettingsAsync(alpha, CancellationToken.None);

        // Имя проекта — вещь отображаемая: на экране не должно остаться двух имён одного
        // проекта — нового в панели и старого на вкладках (раздел 6.3 ТЗ).
        Assert.Equal("omega", alpha.Name);
        Assert.Equal("omega · почини сборку", first.Title);
        Assert.Equal("omega · " + TabViewModel.NewSessionTitle, second!.Title);
        Assert.Equal(1, titleChanges);

        // Вкладка чужого проекта переименования не заметила.
        Assert.Equal("beta · " + TabViewModel.NewSessionTitle, stranger!.Title);
    }

    [Fact]
    public async Task Cancelled_settings_dialog_leaves_the_tab_titles_alone()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        // Edit не задан: диалог вернул null — менять заголовки не с чего.
        await harness.Shell.ShowProjectSettingsAsync(harness.Row(0), CancellationToken.None);

        Assert.Equal("alpha · " + TabViewModel.NewSessionTitle, tab!.Title);
    }

    [Fact]
    public async Task Moved_project_leaves_open_tabs_in_the_directory_they_started_in()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        var opened = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        harness.Probe.Add(PathB);
        harness.Dialog.Edit = project => project with { Path = PathB };
        await harness.Shell.ShowProjectSettingsAsync(row, CancellationToken.None);

        ITabStateSink sink = harness.Shell;

        // Псевдоконсоль работает там, где её запустили, и транскрипт сессии лежит в slug'е
        // прежнего каталога. Отдай координатор новый путь — он не нашёл бы транскрипт,
        // списал бы попытку из бюджета, и вкладка молча осталась бы «новой сессией».
        Assert.True(sink.TryGetWorkingDirectory(opened!.TerminalId, out var directory));
        Assert.Equal(PathA, directory);

        // А сессия, открытая после переноса, стартует уже в новом каталоге.
        var afterMove = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        Assert.Equal(PathB, harness.Workspace.OpenedDirectories[^1]);
        Assert.True(sink.TryGetWorkingDirectory(afterMove!.TerminalId, out var movedDirectory));
        Assert.Equal(PathB, movedDirectory);
    }

    [Fact]
    public async Task A_closed_tab_has_no_working_directory()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        ITabStateSink sink = harness.Shell;
        Assert.False(sink.TryGetWorkingDirectory(tab!.TerminalId, out var directory));
        Assert.Equal(string.Empty, directory);
    }

    [Fact]
    public void Project_settings_are_available_for_the_row_of_the_menu_even_with_nothing_selected()
    {
        var harness = new Harness(Project("alpha", PathA, 0));

        // Параметр приезжает привязкой к PlacementTarget и на первом вычислении может быть
        // ещё не разрешён. Проверяется не он, а строка: переданная либо выбранная.
        Assert.Null(harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.ShowSettingsCommand.CanExecute(null));
        Assert.True(harness.Shell.ShowSettingsCommand.CanExecute(new ProjectRowViewModel(
            Project("alpha", PathA, 0))));
    }

    [Fact]
    public async Task Project_settings_without_a_parameter_follow_the_selected_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        // Кнопка в заголовке окна параметра не передаёт: без выбранной строки настраивать
        // нечего и пункт выключен, с выбранной — доступен.
        Assert.False(harness.Shell.ShowSettingsCommand.CanExecute(null));

        await harness.Shell.ActivateProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.True(harness.Shell.ShowSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Removing_a_project_drops_the_row_and_renumbers_the_rest()
    {
        var harness = await StartedAsync(
            Project("alpha", PathA, 0),
            Project("beta", PathB, 1));
        var alpha = harness.Row(0);

        var removed = await harness.Shell.RemoveProjectAsync(alpha, CancellationToken.None);

        Assert.True(removed);
        Assert.Equal("beta", Assert.Single(harness.Shell.Projects.Rows).Name);

        var saved = Assert.Single(harness.Store.Saved);
        Assert.Equal("beta", saved.Name);

        // Порядок оставшихся пересчитан: дыр в Order не остаётся ни в файле, ни в строке.
        Assert.Equal(0, saved.Order);
        Assert.Equal(0, harness.Row(0).Project.Order);
    }

    [Fact]
    public async Task Declined_removal_keeps_the_project_in_the_list()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        harness.Prompt.ConfirmResult = false;

        var removed = await harness.Shell.RemoveProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.False(removed);
        Assert.Single(harness.Shell.Projects.Rows);
        Assert.Equal(0, harness.Store.SaveCount);
        Assert.Single(harness.Prompt.Confirmations);
    }

    [Fact]
    public async Task Removing_a_project_closes_its_live_tabs()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);

        var first = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        await harness.Shell.RemoveProjectAsync(row, CancellationToken.None);

        // Число сессий названо в подтверждении: закрытие вкладок не должно быть сюрпризом.
        Assert.Contains("2", Assert.Single(harness.Prompt.Confirmations), StringComparison.Ordinal);

        Assert.Equal([first!.TerminalId, second!.TerminalId], harness.Workspace.Closed);
        Assert.Empty(harness.Shell.Tabs.AllTabs);
        Assert.Empty(harness.Shell.Tabs.Tabs);
        Assert.Null(harness.Shell.Tabs.ActiveTab);

        // Выбранной строки больше нет: на месте терминала заглушка.
        Assert.Null(harness.Shell.ActiveProjectRow);
        Assert.False(harness.Shell.IsTerminalVisible);
    }

    [Fact]
    public async Task Removing_an_unselected_project_leaves_the_other_project_alone()
    {
        var harness = await StartedAsync(
            Project("alpha", PathA, 0),
            Project("beta", PathB, 1));
        var alpha = harness.Row(0);
        var beta = harness.Row(1);

        var doomed = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);
        var kept = await harness.Shell.OpenSessionAsync(beta, CancellationToken.None);

        await harness.Shell.RemoveProjectAsync(alpha, CancellationToken.None);

        Assert.Equal(doomed!.TerminalId, Assert.Single(harness.Workspace.Closed));
        Assert.Same(kept, Assert.Single(harness.Shell.Tabs.AllTabs));

        // Выбор не сдвинулся: убрали не тот проект, на который смотрит полоса.
        Assert.Same(beta, harness.Shell.ActiveProjectRow);
        Assert.True(beta.IsCurrent);
        Assert.Same(kept, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(1, beta.SessionCount);
    }

    [Fact]
    public async Task Removal_that_failed_to_save_keeps_both_the_row_and_its_tabs()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        harness.Store.SaveFailure = new InvalidOperationException("projects.json занят");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Shell.RemoveProjectAsync(row, CancellationToken.None));

        Assert.Same(row, Assert.Single(harness.Shell.Projects.Rows));
        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.AllTabs));
        Assert.Empty(harness.Workspace.Closed);
    }

    [Fact]
    public async Task A_tab_that_refused_to_close_does_not_strand_the_rest_of_the_project()
    {
        var harness = await StartedAsync(
            Project("alpha", PathA, 0),
            Project("beta", PathB, 1));
        var alpha = harness.Row(0);
        var beta = harness.Row(1);

        var kept = await harness.Shell.OpenSessionAsync(beta, CancellationToken.None);
        var first = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);

        harness.Workspace.CloseFailure = (first!.TerminalId, new InvalidOperationException("страница ушла"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Shell.RemoveProjectAsync(alpha, CancellationToken.None));

        // Закрытие дошло до обеих вкладок: сбой на первой не отменяет закрытия второй.
        Assert.Equal([first.TerminalId, second!.TerminalId], harness.Workspace.Closed);

        // Строки проекта в списке уже нет, поэтому и вкладок его не осталось: иначе они
        // жили бы с псевдоконсолями и без способа выбрать их проект.
        Assert.Same(kept, Assert.Single(harness.Shell.Tabs.AllTabs));
        Assert.Same(beta, Assert.Single(harness.Shell.Projects.Rows));
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.Null(harness.Shell.ActiveProjectRow);

        // Счётчики пересчитаны и на сбое: у уцелевшего проекта своя вкладка на месте.
        Assert.Equal(1, beta.SessionCount);
    }

    [Fact]
    public async Task Removing_the_project_of_the_active_tab_leaves_no_active_tab()
    {
        var harness = await StartedAsync(
            Project("alpha", PathA, 0),
            Project("beta", PathB, 1));
        var alpha = harness.Row(0);

        var stranger = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        var doomed = await harness.Shell.OpenSessionAsync(alpha, CancellationToken.None);
        Assert.Same(doomed, harness.Shell.Tabs.ActiveTab);

        await harness.Shell.RemoveProjectAsync(alpha, CancellationToken.None);

        // Выбор не остаётся на закрытой вкладке: соседней в убранном проекте не осталось,
        // а вкладка чужого проекта активной не становится сама.
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.False(doomed!.IsActive);
        Assert.False(stranger!.IsActive);
        Assert.Same(stranger, Assert.Single(harness.Shell.Tabs.AllTabs));
    }

    [Fact]
    public async Task A_shell_that_exited_normally_is_marked_with_a_zero_code()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        harness.Workspace.RaiseExited(tab!.TerminalId, exitCode: 0);

        // Вкладка остаётся на экране с пометкой о коде (раздел 8 ТЗ), но это штатный выход
        // из оболочки: падением он не считается и красным не красится.
        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.True(tab.HasExited);
        Assert.False(tab.IsRunning);
        Assert.Equal(0, tab.ExitCode);
        Assert.False(tab.HasFailedExit);
        Assert.Equal("код 0", tab.ExitBadgeText);
    }

    [Fact]
    public async Task A_shell_that_crashed_is_marked_with_its_exit_code()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        var changed = new List<string?>();
        tab!.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Оболочку сняли по Ctrl+C: код падения у Windows отрицательный, и пометка обязана
        // показать его как есть, а не «упал».
        harness.Workspace.RaiseExited(tab.TerminalId, exitCode: -1073741510);

        Assert.True(tab.HasFailedExit);
        Assert.Equal(-1073741510, tab.ExitCode);
        Assert.Equal("код -1073741510", tab.ExitBadgeText);

        // Без уведомления об этих свойствах пометка и кнопка не появились бы на живой полосе.
        Assert.Contains(nameof(TabViewModel.HasExited), changed);
        Assert.Contains(nameof(TabViewModel.ExitBadgeText), changed);
        Assert.Contains(nameof(TabViewModel.HasFailedExit), changed);
    }

    [Fact]
    public async Task A_live_tab_has_no_badge_and_no_restart()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.False(tab!.HasExited);
        Assert.Null(tab.ExitCode);
        Assert.Equal(string.Empty, tab.ExitBadgeText);
        Assert.False(harness.Shell.RestartTabCommand.CanExecute(tab));

        // Перезапускать живую сессию нечего: второй вкладки не появляется.
        Assert.Null(await harness.Shell.RestartTabAsync(tab, CancellationToken.None));
        Assert.Same(tab, Assert.Single(harness.Shell.Tabs.Tabs));
    }

    [Fact]
    public async Task Restart_opens_a_working_session_next_to_the_dead_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var dead = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(dead!.TerminalId, exitCode: 1);

        Assert.True(harness.Shell.RestartTabCommand.CanExecute(dead));
        var restarted = await harness.Shell.RestartTabAsync(dead, CancellationToken.None);

        // Мёртвая вкладка не заменяется: её вывод пользователь ещё не прочитал.
        Assert.NotNull(restarted);
        Assert.NotSame(dead, restarted);
        Assert.Equal([dead, restarted], harness.Shell.Tabs.Tabs);
        Assert.True(dead.HasExited);

        // Новая сессия живая, видимая и в том же проекте.
        Assert.True(restarted!.IsRunning);
        Assert.Same(restarted, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(restarted.TerminalId, harness.Workspace.VisibleTerminal);
        Assert.Equal(dead.ProjectId, restarted.ProjectId);
        Assert.Equal([PathA, PathA], harness.Workspace.OpenedDirectories);
        Assert.Equal(2, harness.Row(0).SessionCount);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Restart_into_a_vanished_folder_is_blocked_and_keeps_the_dead_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var dead = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(dead!.TerminalId, exitCode: 1);

        // Каталог исчез, пока вкладка лежала мёртвой (раздел 8 ТЗ).
        harness.Probe.Remove(PathA);

        var restarted = await harness.Shell.RestartTabAsync(dead, CancellationToken.None);

        // Запуск заблокирован той же проверкой доступности, что и обычное открытие сессии:
        // пользователь видит сообщение, а не исключение.
        Assert.Null(restarted);
        Assert.Contains(PathA, Assert.Single(harness.Prompt.Errors));
        Assert.False(harness.Row(0).IsAvailable);
        Assert.Same(dead, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Equal([PathA], harness.Workspace.OpenedDirectories);
    }

    [Fact]
    public async Task Restart_of_a_tab_whose_project_is_gone_does_nothing()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var dead = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(dead!.TerminalId, exitCode: 1);

        // Строку убрали из списка вместе с вкладкой: запускать не в чем, но и падать не за что.
        await harness.Shell.RemoveProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Null(await harness.Shell.RestartTabAsync(dead, CancellationToken.None));
        Assert.Empty(harness.Shell.Tabs.AllTabs);
    }

    [Fact]
    public async Task Closing_a_dead_tab_works_as_usual()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var dead = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var alive = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(dead!.TerminalId, exitCode: 1);

        Assert.True(await harness.Shell.CloseTabAsync(dead, CancellationToken.None));

        // Ни вопроса (процесса под вкладкой уже нет), ни следов в полосе и на строке проекта.
        Assert.Empty(harness.Prompt.Confirmations);
        Assert.Equal(dead.TerminalId, Assert.Single(harness.Workspace.Closed));
        Assert.Same(alive, Assert.Single(harness.Shell.Tabs.Tabs));
        Assert.Equal(1, harness.Row(0).SessionCount);
    }

    [Fact]
    public async Task A_row_without_tabs_keeps_its_marker_unknown()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));

        Assert.Equal(TabState.Unknown, harness.Row(0).MarkerState);
    }

    [Fact]
    public async Task Row_marker_repaints_when_a_hook_changes_the_state_of_an_open_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Состав вкладок не меняется — меняется только состояние. Точка на строке обязана
        // перекраситься без переоткрытия вкладок, иначе она застынет на значении,
        // вычисленном при открытии сессии.
        tab!.State = TabState.Busy;

        Assert.Equal(TabState.Busy, row.MarkerState);
        Assert.Contains(nameof(ProjectRowViewModel.MarkerState), changed);

        tab.State = TabState.AwaitingInput;

        Assert.Equal(TabState.AwaitingInput, row.MarkerState);
    }

    [Fact]
    public async Task Row_marker_of_a_project_that_is_not_shown_repaints_too()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var beta = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        beta!.State = TabState.AwaitingInput;

        // На экране вкладки alpha, но точка beta должна загореться: ради этого маркер
        // и нужен — увидеть проект, до которого ещё не дошли.
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);
        Assert.Equal(TabState.AwaitingInput, harness.Row(1).MarkerState);
        Assert.Equal(TabState.Unknown, harness.Row(0).MarkerState);
    }

    [Fact]
    public async Task Row_marker_sums_up_the_tabs_of_the_project()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        var idle = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        var busy = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);

        idle!.State = TabState.Idle;
        busy!.State = TabState.Busy;

        Assert.Equal(TabState.Busy, row.MarkerState);

        busy.State = TabState.AwaitingInput;

        Assert.Equal(TabState.AwaitingInput, row.MarkerState);
    }

    [Fact]
    public async Task Closing_the_last_tab_of_a_project_returns_the_marker_to_unknown()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var row = harness.Row(0);
        var tab = await harness.Shell.OpenSessionAsync(row, CancellationToken.None);
        tab!.State = TabState.AwaitingInput;
        Assert.Equal(TabState.AwaitingInput, row.MarkerState);

        await harness.Shell.CloseTabAsync(tab, CancellationToken.None);

        // Вкладок не осталось — светиться нечему, и точка в разметке всё равно скрыта.
        Assert.Equal(TabState.Unknown, row.MarkerState);
        Assert.False(row.HasSessions);
    }

    private static List<TerminalId> RecordClosedTabs(ShellViewModel shell)
    {
        var closed = new List<TerminalId>();
        ((IDiffTabs)shell).TabClosed += (_, e) => closed.Add(e.TerminalId);
        return closed;
    }

    [Fact]
    public async Task Closing_a_tab_tells_the_diff_panel_which_tab_is_gone()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var closed = RecordClosedTabs(harness.Shell);
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(first!.TerminalId, 0);

        await harness.Shell.CloseTabAsync(first, CancellationToken.None);

        Assert.Equal([first.TerminalId], closed);
        Assert.Null(((IDiffTabs)harness.Shell).Find(first.TerminalId));
    }

    [Fact]
    public async Task Refused_close_keeps_the_diff_panel_of_the_tab()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var closed = RecordClosedTabs(harness.Shell);
        var tab = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Prompt.ConfirmResult = false;

        await harness.Shell.CloseTabAsync(tab!, CancellationToken.None);

        Assert.Empty(closed);
    }

    [Fact]
    public async Task Removing_a_project_reports_every_one_of_its_tabs_as_closed()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));
        var closed = RecordClosedTabs(harness.Shell);
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var second = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        var foreign = await harness.Shell.OpenSessionAsync(harness.Row(1), CancellationToken.None);

        await harness.Shell.RemoveProjectAsync(harness.Row(0), CancellationToken.None);

        Assert.Equal([first!.TerminalId, second!.TerminalId], closed);
        Assert.Same(foreign, ((IDiffTabs)harness.Shell).Find(foreign!.TerminalId));
    }

    [Fact]
    public async Task Restart_keeps_the_dead_tab_and_its_diff_panel()
    {
        // Перезапуск открывает соседнюю вкладку, а мёртвая остаётся: закрывать её панель рано.
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var closed = RecordClosedTabs(harness.Shell);
        var dead = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        harness.Workspace.RaiseExited(dead!.TerminalId, exitCode: 1);

        await harness.Shell.RestartTabAsync(dead, CancellationToken.None);

        Assert.Empty(closed);
        Assert.Same(dead, ((IDiffTabs)harness.Shell).Find(dead.TerminalId));
    }

    [Fact]
    public async Task Diff_button_activates_the_tab_and_opens_its_panel()
    {
        var harness = await StartedAsync(Project("alpha", PathA, 0));
        var first = await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Row(0), CancellationToken.None);

        Assert.True(harness.Shell.ShowDiffCommand.CanExecute(first));
        await harness.Shell.ShowDiffAsync(first!, CancellationToken.None);

        Assert.Same(first, harness.Shell.Tabs.ActiveTab);
        var index = Assert.Single(harness.Diff.View.CallsOf("index"));
        Assert.Equal(first!.TerminalId, index.TerminalId);
        Assert.Equal([PathA], harness.Diff.Git.Requests.Select(request => request.Directory));
    }

    [Fact]
    public async Task Diff_starts_listening_to_the_page_on_initialization_and_stops_on_dispose()
    {
        var harness = new Harness(Project("alpha", PathA, 0));
        Assert.False(harness.Diff.View.HasSubscribers);

        await harness.InitializeAsync();
        Assert.True(harness.Diff.View.HasSubscribers);

        await harness.Shell.DisposeAsync();
        Assert.False(harness.Diff.View.HasSubscribers);
    }
}
