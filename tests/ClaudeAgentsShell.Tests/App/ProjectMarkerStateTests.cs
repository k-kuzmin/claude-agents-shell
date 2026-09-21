using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Точка состояния на строке проекта (раздел 6.2 ТЗ): одно состояние на проект, сведённое
/// по состояниям его вкладок. Проверяется свод — <see cref="TabStripViewModel.MarkerStateFor" />;
/// цвета живут в разметке и тестом не берутся.
/// <para>
/// Свод идёт по **всем** вкладкам проекта, а не по показанным в полосе: проект, который
/// сейчас не выбран, тоже обязан светиться. И наоборот — вкладки чужих проектов на точку
/// влиять не должны.
/// </para>
/// </summary>
public sealed class ProjectMarkerStateTests
{
    private static readonly Guid FirstProject = Guid.NewGuid();
    private static readonly Guid SecondProject = Guid.NewGuid();

    [Fact]
    public void A_project_without_tabs_has_no_marker()
    {
        var strip = new TabStripViewModel();

        Assert.Equal(TabState.Unknown, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void A_single_tab_gives_the_project_its_own_state()
    {
        var strip = new TabStripViewModel();
        var tab = Add(strip, FirstProject);

        tab.State = TabState.Busy;

        Assert.Equal(TabState.Busy, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void Awaiting_input_outranks_every_other_state()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, TabState.Idle, TabState.Busy, TabState.BackgroundWork);
        tabs[0].State = TabState.AwaitingInput;

        // «Меня ждут» важнее всего: иначе занятая соседка спрятала бы ждущую сессию.
        Assert.Equal(TabState.AwaitingInput, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void Busy_outranks_background_work()
    {
        var strip = new TabStripViewModel();
        Open(strip, FirstProject, TabState.BackgroundWork, TabState.Busy, TabState.Idle);

        // Порядок приоритета намеренно не совпадает с числовым порядком TabState:
        // BackgroundWork объявлен последним, но по важности стоит ниже Busy.
        Assert.Equal(TabState.Busy, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void Background_work_outranks_idle()
    {
        var strip = new TabStripViewModel();
        Open(strip, FirstProject, TabState.Idle, TabState.BackgroundWork, TabState.Idle);

        Assert.Equal(TabState.BackgroundWork, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void Idle_outranks_unknown()
    {
        var strip = new TabStripViewModel();
        Open(strip, FirstProject, TabState.Unknown, TabState.Idle);

        // Хуки не доехали только до одной вкладки — это не повод гасить точку проекта.
        Assert.Equal(TabState.Idle, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void Tabs_of_other_projects_do_not_touch_the_marker()
    {
        var strip = new TabStripViewModel();
        Open(strip, FirstProject, TabState.Idle);
        Open(strip, SecondProject, TabState.AwaitingInput);

        Assert.Equal(TabState.Idle, strip.MarkerStateFor(FirstProject));
        Assert.Equal(TabState.AwaitingInput, strip.MarkerStateFor(SecondProject));
    }

    [Fact]
    public void Hidden_tabs_count_towards_the_marker()
    {
        var strip = new TabStripViewModel();
        Open(strip, FirstProject, TabState.AwaitingInput);
        strip.ShowProject(SecondProject);

        // Полоса показывает второй проект, но точка первого обязана гореть: смысл маркера —
        // заметить сессию в проекте, которого сейчас нет на экране.
        Assert.Empty(strip.Tabs);
        Assert.Equal(TabState.AwaitingInput, strip.MarkerStateFor(FirstProject));
    }

    [Fact]
    public void A_state_change_of_an_open_tab_bumps_the_revision()
    {
        var strip = new TabStripViewModel();
        var tab = Add(strip, FirstProject);

        var before = strip.StateRevision;
        var changed = new List<string?>();
        strip.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.State = TabState.Busy;

        // Состав вкладок не изменился, и без этого признака снаружи не узнать, что точки
        // на строках проектов устарели.
        Assert.True(strip.StateRevision > before);
        Assert.Contains(nameof(TabStripViewModel.StateRevision), changed);
    }

    private static List<TabViewModel> Open(TabStripViewModel strip, Guid projectId, params TabState[] states)
    {
        var tabs = new List<TabViewModel>(states.Length);
        foreach (var state in states)
        {
            var tab = Add(strip, projectId);
            tab.State = state;
            tabs.Add(tab);
        }

        return tabs;
    }

    private static TabViewModel Add(TabStripViewModel strip, Guid projectId)
    {
        var tab = new TabViewModel(TerminalId.New(), projectId, "проект", @"D:\src\alpha");
        strip.Add(tab);
        return tab;
    }
}
