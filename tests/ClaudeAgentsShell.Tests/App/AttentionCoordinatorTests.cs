using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Сигнал «ждёт ввода» вне приложения: мигание в панели задач, уведомления и клик по ним.
/// Координатор собирается поверх настоящей корневой ViewModel — закрытие вкладок и
/// удаление проекта проверяются теми же путями, что у пользователя.
/// </summary>
public sealed class AttentionCoordinatorTests
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
                Store, new FakeGitBranchReader(), new FakeGitBranchWatcher(), Probe, new FakeFolderPicker(),
                new FakeProjectSettingsDialog(), Prompt, new FakeShellLauncher(), new InlineUiDispatcher());
            var sessionState = new SessionStateCoordinator(
                new FakeHookListener(), Workspace, new FakeSessionHistoryReader(), new InlineUiDispatcher());
            var diff = new ShellDiffParts();
            Shell = new ShellViewModel(
                Workspace, list, Prompt, new InlineUiDispatcher(), sessionState, new FakeLayoutStore().CreateService(),
                diff.Coordinator, diff.Tracker, new FakeAppVersion("1.2.3+abc"), new FakeSessionHistoryDialog());
            Coordinator = new AttentionCoordinator(Shell.Tabs, Shell, Focus, Taskbar, Toasts, Reveal, Prompt);
        }

        public FakeProjectStore Store { get; } = new();

        public FakeDirectoryProbe Probe { get; } = new();

        public FakeUserPrompt Prompt { get; } = new();

        public FakeTerminalWorkspace Workspace { get; } = new();

        public FakeAppFocus Focus { get; } = new();

        public FakeTaskbarAttention Taskbar { get; } = new();

        public FakeAwaitingToasts Toasts { get; } = new();

        public FakeMainWindowReveal Reveal { get; } = new();

        public ShellViewModel Shell { get; }

        public AttentionCoordinator Coordinator { get; }

        public ProjectRowViewModel Row(int index) => Shell.Projects.Rows[index];

        public async Task<TabViewModel> OpenAsync(int row)
        {
            var tab = await Shell.OpenSessionAsync(Row(row), CancellationToken.None);
            return tab!;
        }
    }

    private static async Task<Harness> StartedAsync(params ProjectDefinition[] projects)
    {
        var harness = new Harness(projects);
        await harness.Shell.InitializeAsync(CancellationToken.None);
        return harness;
    }

    private static Task<Harness> AlphaAndBetaAsync() =>
        StartedAsync(Project("alpha", PathA, 0), Project("beta", PathB, 1));

    [Fact]
    public async Task Вкладка_ждёт_ввода_при_неактивном_приложении_мигает_и_показывает_уведомление()
    {
        var harness = await AlphaAndBetaAsync();
        var tab = await harness.OpenAsync(0);

        tab.State = TabState.AwaitingInput;

        Assert.Equal(1, harness.Taskbar.Requests);
        var toast = Assert.Single(harness.Toasts.Shown);
        Assert.Equal(tab.TerminalId, toast.Tab);
        Assert.Equal("alpha · " + TabViewModel.NewSessionTitle, toast.Title);
    }

    [Fact]
    public async Task При_активном_приложении_ни_мигания_ни_уведомления()
    {
        var harness = await AlphaAndBetaAsync();
        harness.Focus.IsActive = true;
        var tab = await harness.OpenAsync(0);

        tab.State = TabState.AwaitingInput;
        tab.State = TabState.Busy;

        Assert.Equal(0, harness.Taskbar.Requests);
        Assert.Equal(0, harness.Taskbar.Cancels);
        Assert.Empty(harness.Toasts.Shown);
    }

    [Fact]
    public async Task Вторая_ждущая_вкладка_перезапускает_мигание()
    {
        var harness = await AlphaAndBetaAsync();
        var first = await harness.OpenAsync(0);
        var second = await harness.OpenAsync(1);

        first.State = TabState.AwaitingInput;
        second.State = TabState.AwaitingInput;

        // Решение пользователя: мигание начинается заново на каждый новый переход,
        // а не только когда ждущих не было.
        Assert.Equal(2, harness.Taskbar.Requests);
        Assert.Equal(new[] { first.TerminalId, second.TerminalId }, harness.Toasts.Shown.Select(toast => toast.Tab));
    }

    [Fact]
    public async Task Активация_приложения_снимает_мигание_и_все_уведомления_даже_при_двух_ждущих()
    {
        var harness = await AlphaAndBetaAsync();
        var first = await harness.OpenAsync(0);
        var second = await harness.OpenAsync(1);
        first.State = TabState.AwaitingInput;
        second.State = TabState.AwaitingInput;

        harness.Focus.Activate();

        Assert.Equal(1, harness.Taskbar.Cancels);
        Assert.Equal(1, harness.Toasts.Clears);
        Assert.Equal(2, harness.Shell.Tabs.AwaitingInputCount);
    }

    [Fact]
    public async Task Вкладка_ушла_из_ожидания_её_уведомление_снимается()
    {
        var harness = await AlphaAndBetaAsync();
        var tab = await harness.OpenAsync(0);
        tab.State = TabState.AwaitingInput;

        tab.State = TabState.Busy;

        Assert.Equal(tab.TerminalId, Assert.Single(harness.Toasts.Removed));
    }

    [Fact]
    public async Task Мигание_снимается_только_когда_ждущих_не_осталось()
    {
        var harness = await AlphaAndBetaAsync();
        var first = await harness.OpenAsync(0);
        var second = await harness.OpenAsync(1);
        first.State = TabState.AwaitingInput;
        second.State = TabState.AwaitingInput;

        first.State = TabState.Busy;
        Assert.Equal(0, harness.Taskbar.Cancels);

        second.State = TabState.Idle;
        Assert.Equal(1, harness.Taskbar.Cancels);
        Assert.Equal(new[] { first.TerminalId, second.TerminalId }, harness.Toasts.Removed);
    }

    [Fact]
    public async Task Вкладка_закрытая_в_ожидании_снимает_уведомление_и_мигание()
    {
        var harness = await AlphaAndBetaAsync();
        var tab = await harness.OpenAsync(0);
        tab.State = TabState.AwaitingInput;

        Assert.True(await harness.Shell.CloseTabAsync(tab, CancellationToken.None));

        Assert.Equal(tab.TerminalId, Assert.Single(harness.Toasts.Removed));
        Assert.Equal(1, harness.Taskbar.Cancels);
    }

    [Fact]
    public async Task Удалённый_проект_с_ждущей_вкладкой_снимает_её_уведомление()
    {
        var harness = await AlphaAndBetaAsync();
        var doomed = await harness.OpenAsync(0);
        var kept = await harness.OpenAsync(1);
        doomed.State = TabState.AwaitingInput;
        kept.State = TabState.AwaitingInput;

        Assert.True(await harness.Shell.RemoveProjectAsync(harness.Row(0), CancellationToken.None));

        Assert.Equal(doomed.TerminalId, Assert.Single(harness.Toasts.Removed));

        // Вкладка другого проекта ещё ждёт — мигание остаётся.
        Assert.Equal(0, harness.Taskbar.Cancels);
    }

    [Fact]
    public async Task Мёртвая_вкладка_выходит_из_ожидания_и_снимает_уведомление()
    {
        var harness = await AlphaAndBetaAsync();
        var tab = await harness.OpenAsync(0);
        tab.State = TabState.AwaitingInput;

        harness.Workspace.RaiseExited(tab.TerminalId, exitCode: 1);

        Assert.Equal(tab.TerminalId, Assert.Single(harness.Toasts.Removed));
        Assert.Equal(1, harness.Taskbar.Cancels);
    }

    [Fact]
    public async Task Клик_по_уведомлению_закрытой_вкладки_только_поднимает_окно()
    {
        var harness = await AlphaAndBetaAsync();
        var closed = await harness.OpenAsync(0);
        var other = await harness.OpenAsync(1);
        closed.State = TabState.AwaitingInput;
        await harness.Shell.CloseTabAsync(closed, CancellationToken.None);

        harness.Toasts.Click(closed.TerminalId);

        Assert.Equal(1, harness.Reveal.Reveals);
        Assert.Same(other, harness.Shell.Tabs.ActiveTab);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Клик_по_уведомлению_вкладки_убранного_проекта_только_поднимает_окно()
    {
        var harness = await AlphaAndBetaAsync();
        var doomed = await harness.OpenAsync(0);
        doomed.State = TabState.AwaitingInput;
        await harness.Shell.RemoveProjectAsync(harness.Row(0), CancellationToken.None);

        harness.Toasts.Click(doomed.TerminalId);

        Assert.Equal(1, harness.Reveal.Reveals);
        Assert.Null(harness.Shell.Tabs.ActiveTab);
        Assert.Empty(harness.Prompt.Errors);
    }

    [Fact]
    public async Task Клик_по_уведомлению_вкладки_другого_проекта_поднимает_окно_и_открывает_её()
    {
        var harness = await AlphaAndBetaAsync();
        var waiting = await harness.OpenAsync(1);
        await harness.OpenAsync(0);
        waiting.State = TabState.AwaitingInput;
        Assert.Same(harness.Row(0), harness.Shell.ActiveProjectRow);

        harness.Toasts.Click(waiting.TerminalId);

        Assert.Equal(1, harness.Reveal.Reveals);
        Assert.Same(harness.Row(1), harness.Shell.ActiveProjectRow);
        Assert.Same(waiting, harness.Shell.Tabs.ActiveTab);
        Assert.Equal(waiting.TerminalId, harness.Workspace.VisibleTerminal);
    }

    [Fact]
    public void Сбой_перехода_по_клику_показывается_пользователю_а_не_роняет_приложение()
    {
        var prompt = new FakeUserPrompt();
        var reveal = new FakeMainWindowReveal();
        var toasts = new FakeAwaitingToasts();
        using var coordinator = new AttentionCoordinator(
            new TabStripViewModel(), new FailingTabNavigation(), new FakeAppFocus(),
            new FakeTaskbarAttention(), toasts, reveal, prompt);

        toasts.Click(TerminalId.New());

        // Окно поднимается раньше перехода — и остаётся поднятым, когда переход сорвался.
        Assert.Equal(1, reveal.Reveals);
        Assert.Equal("мост недоступен", Assert.Single(prompt.Errors));
    }

    [Fact]
    public async Task После_освобождения_координатор_ни_на_что_не_реагирует()
    {
        var harness = await AlphaAndBetaAsync();
        var tab = await harness.OpenAsync(0);

        harness.Coordinator.Dispose();
        tab.State = TabState.AwaitingInput;
        harness.Focus.Activate();
        harness.Toasts.Click(tab.TerminalId);

        Assert.Equal(0, harness.Taskbar.Requests);
        Assert.Equal(0, harness.Taskbar.Cancels);
        Assert.Equal(0, harness.Toasts.Clears);
        Assert.Equal(0, harness.Reveal.Reveals);
    }

    // Грани полосы вкладок — источник координатора.

    private static TabViewModel Tab(Guid projectId) =>
        new(TerminalId.New(), projectId, "проект", PathA);

    [Fact]
    public void Одновременная_смена_ждущих_вкладок_даёт_грань_для_новой()
    {
        var strip = new TabStripViewModel();
        var projectId = Guid.NewGuid();
        var leaving = Tab(projectId);
        var arriving = Tab(projectId);
        strip.Add(leaving);
        strip.Add(arriving);
        leaving.State = TabState.AwaitingInput;

        var became = new List<TabViewModel>();
        var left = new List<TabViewModel>();
        strip.TabBecameAwaiting += (_, tab) => became.Add(tab);
        strip.TabLeftAwaiting += (_, tab) => left.Add(tab);

        leaving.State = TabState.Busy;
        arriving.State = TabState.AwaitingInput;

        // Счётчик тот же, а грани различают, кто ушёл и кто пришёл.
        Assert.Equal(1, strip.AwaitingInputCount);
        Assert.Same(leaving, Assert.Single(left));
        Assert.Same(arriving, Assert.Single(became));
    }

    [Fact]
    public void К_моменту_грани_ухода_счётчик_уже_пересчитан()
    {
        var strip = new TabStripViewModel();
        var tab = Tab(Guid.NewGuid());
        strip.Add(tab);
        tab.State = TabState.AwaitingInput;

        bool? hadAwaiting = null;
        strip.TabLeftAwaiting += (_, _) => hadAwaiting = strip.HasAwaitingInput;

        tab.State = TabState.Idle;

        Assert.False(hadAwaiting);
    }

    [Fact]
    public void Повторное_ожидание_той_же_вкладки_не_даёт_второй_грани()
    {
        var strip = new TabStripViewModel();
        var tab = Tab(Guid.NewGuid());
        strip.Add(tab);

        var became = 0;
        strip.TabBecameAwaiting += (_, _) => became++;

        tab.State = TabState.AwaitingInput;
        tab.State = TabState.AwaitingInput;

        Assert.Equal(1, became);
    }

    [Fact]
    public void Вкладка_добавленная_уже_ждущей_даёт_грань_а_убранная_грань_ухода()
    {
        var strip = new TabStripViewModel();
        var tab = Tab(Guid.NewGuid());
        tab.State = TabState.AwaitingInput;

        var became = new List<TabViewModel>();
        var left = new List<TabViewModel>();
        strip.TabBecameAwaiting += (_, t) => became.Add(t);
        strip.TabLeftAwaiting += (_, t) => left.Add(t);

        strip.Add(tab);
        Assert.Same(tab, Assert.Single(became));
        Assert.Equal(1, strip.AwaitingInputCount);

        strip.Remove(tab);
        Assert.Same(tab, Assert.Single(left));
        Assert.Equal(0, strip.AwaitingInputCount);

        // Отписка при удалении: закрытая вкладка больше не даёт граней.
        tab.State = TabState.Idle;
        Assert.Single(left);
    }
}
