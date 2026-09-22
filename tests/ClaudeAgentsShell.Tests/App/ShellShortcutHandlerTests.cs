using System.Windows.Input;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Приём клавиатуры окном: что перехватывается и что доходит до терминала.</summary>
public sealed class ShellShortcutHandlerTests
{
    private sealed class Harness
    {
        public Harness()
        {
            var project = new ProjectDefinition(
                Guid.NewGuid(), "alpha", @"D:\src\alpha", ShellKind.Pwsh, null, [], 0);

            Store.Seed(project);
            Probe.Add(project.Path);

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
            var layouts = new FakeLayoutStore();
            Shell = new ShellViewModel(
                Workspace, list, Prompt, new InlineUiDispatcher(), sessionState, layouts.CreateService(),
                Diff.Coordinator, Diff.Tracker);
            Handler = new ShellShortcutHandler(Shell, Prompt);
        }

        public FakeProjectStore Store { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public ShellDiffParts Diff { get; } = new();

        public FakeTerminalWorkspace Workspace { get; } = new();

        public ShellViewModel Shell { get; }

        public ShellShortcutHandler Handler { get; }
    }

    private static async Task<Harness> StartedAsync()
    {
        var harness = new Harness();
        await harness.Shell.InitializeAsync(CancellationToken.None);
        return harness;
    }

    [Fact]
    public async Task Ctrl_shift_t_opens_a_session_in_the_active_project()
    {
        var harness = await StartedAsync();
        await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);

        var handled = harness.Handler.Handle(Key.T, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.True(handled);
        Assert.Equal(2, harness.Shell.Tabs.Tabs.Count);
    }

    [Fact]
    public async Task Ctrl_digit_switches_the_tab()
    {
        var harness = await StartedAsync();
        var first = await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);
        await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);

        var handled = harness.Handler.Handle(Key.D1, ModifierKeys.Control);

        Assert.True(handled);
        Assert.Same(first, harness.Shell.Tabs.ActiveTab);
    }

    [Fact]
    public async Task Ctrl_shift_d_opens_the_diff_of_the_active_tab()
    {
        var harness = await StartedAsync();
        await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);
        var active = await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);

        var handled = harness.Handler.Handle(Key.D, ModifierKeys.Control | ModifierKeys.Shift);
        await harness.Diff.Coordinator.WhenIdleAsync();

        Assert.True(handled);
        var index = Assert.Single(harness.Diff.View.CallsOf("index"));
        Assert.Equal(active!.TerminalId, index.TerminalId);
    }

    [Fact]
    public async Task Ctrl_shift_d_without_tabs_does_nothing()
    {
        var harness = await StartedAsync();

        Assert.True(harness.Handler.Handle(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
        await harness.Diff.Coordinator.WhenIdleAsync();

        Assert.Empty(harness.Diff.View.Calls);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Shell_keys_are_not_intercepted()
    {
        var harness = await StartedAsync();
        await harness.Shell.OpenSessionAsync(harness.Shell.Projects.Rows[0], CancellationToken.None);

        // Ctrl+W и Ctrl+T принадлежат оболочке: окно обязано их пропустить.
        Assert.False(harness.Handler.Handle(Key.W, ModifierKeys.Control));
        Assert.False(harness.Handler.Handle(Key.T, ModifierKeys.Control));
        Assert.False(harness.Handler.Handle(Key.C, ModifierKeys.Control));
        Assert.False(harness.Handler.Handle(Key.D, ModifierKeys.Control));

        Assert.Single(harness.Shell.Tabs.Tabs);
        Assert.Empty(harness.Workspace.Closed);
    }
}
