using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>План восстановления, снимок и перенос записей недоступных проектов.</summary>
public sealed class WorkspaceLayoutServiceTests
{
    private static readonly Guid Alpha = Guid.NewGuid();
    private static readonly Guid Beta = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    private static LayoutTabSnapshot Tab(Guid project, string? session, bool live = true, bool active = false) =>
        new(project, session, ShortTitle: session is null ? null : "имя " + session, live, active);

    private static async Task<(WorkspaceLayoutService Service, LayoutRestorePlan Plan, ProjectLayout Offline)> PlanWithOfflineBetaAsync()
    {
        var store = new FakeLayoutStore();
        var offline = new ProjectLayout(Beta, 1, [new TabLayout("b-1", "один"), new TabLayout("b-2", null)]);
        store.Stored = new WorkspaceLayout(
            Gone,
            [
                new ProjectLayout(Gone, 0, [new TabLayout("g-1", null)]),
                offline,
                new ProjectLayout(Alpha, 0, [new TabLayout("a-1", null)]),
            ]);
        var service = store.CreateService();

        var plan = await service.PlanRestoreAsync(
            id => id == Alpha || id == Beta,
            (id, _) => Task.FromResult(id == Alpha),
            CancellationToken.None);

        return (service, plan, offline);
    }

    [Fact]
    public async Task План_поднимает_только_доступные_проекты_и_забывает_убранный_активный()
    {
        var (_, plan, _) = await PlanWithOfflineBetaAsync();

        Assert.Equal(new[] { Alpha }, plan.Projects.Select(static project => project.ProjectId));
        Assert.Null(plan.ActiveProjectId);
    }

    [Fact]
    public void Режим_запуска_по_записи_вкладки()
    {
        Assert.Equal(new SessionLaunch.ResumeSession("s"), LayoutRestorePlan.LaunchFor(new TabLayout("s", null)));
        Assert.Equal(new SessionLaunch.NewSession(), LayoutRestorePlan.LaunchFor(new TabLayout(null, "имя")));
    }

    [Fact]
    public void Снимок_берёт_только_живые_вкладки_в_порядке_полосы_и_панели()
    {
        var service = new FakeLayoutStore().CreateService();

        var layout = service.Capture(
            [Beta, Alpha],
            [
                Tab(Alpha, "a-1"),
                Tab(Alpha, "a-dead", live: false, active: true),
                Tab(Beta, "b-1"),
                Tab(Alpha, "a-2"),
                Tab(Beta, "b-2", active: true),
                Tab(Gone, "g-1"),
            ],
            Beta);

        Assert.Equal(Beta, layout.ActiveProjectId);
        Assert.Equal(new[] { Beta, Alpha }, layout.Projects.Select(static project => project.ProjectId));
        Assert.Equal(new[] { "b-1", "b-2" }, layout.Projects[0].Tabs.Select(static tab => tab.SessionId));
        Assert.Equal(1, layout.Projects[0].ActiveTabIndex);

        // Активная вкладка умерла — активной считается первая живая.
        Assert.Equal(new[] { "a-1", "a-2" }, layout.Projects[1].Tabs.Select(static tab => tab.SessionId));
        Assert.Equal(0, layout.Projects[1].ActiveTabIndex);
        Assert.Equal("имя a-1", layout.Projects[1].Tabs[0].ShortTitle);
    }

    [Fact]
    public async Task Записи_недоступного_проекта_переносятся_в_снимок_как_есть()
    {
        var (service, _, offline) = await PlanWithOfflineBetaAsync();

        var layout = service.Capture([Alpha, Beta], [Tab(Alpha, "a-1")], Alpha);

        Assert.Equal(new[] { Alpha, Beta }, layout.Projects.Select(static project => project.ProjectId));
        Assert.Same(offline, layout.Projects[1]);
    }

    [Fact]
    public async Task Живые_вкладки_проекта_вытесняют_перенесённые_записи()
    {
        var (service, _, _) = await PlanWithOfflineBetaAsync();

        var layout = service.Capture([Beta], [Tab(Beta, "fresh")], Beta);

        Assert.Equal("fresh", Assert.Single(Assert.Single(layout.Projects).Tabs).SessionId);
    }

    [Fact]
    public async Task Забытые_записи_больше_не_переносятся()
    {
        var (service, _, _) = await PlanWithOfflineBetaAsync();

        service.ForgetDeferred(Beta);

        Assert.Empty(service.Capture([Beta], [], Beta).Projects);
    }

    [Fact]
    public async Task Проект_убранный_из_списка_из_снимка_выпадает()
    {
        var (service, _, _) = await PlanWithOfflineBetaAsync();

        Assert.Empty(service.Capture([Alpha], [], Alpha).Projects);
    }
}
