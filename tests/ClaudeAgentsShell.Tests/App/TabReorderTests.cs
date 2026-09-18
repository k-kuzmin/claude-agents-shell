using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Перетаскивание вкладок меняет порядок (раздел 6.3 ТЗ). Проверяется сама перестановка
/// в <see cref="TabStripViewModel" />: мышь и координаты живут в разметке полосы и тестом
/// не берутся, а вот согласованность двух списков — видимого и общего — берётся обязательно.
/// <para>
/// Полоса показывает вкладки только выбранного проекта, поэтому у перестановки два
/// следствия: видимый порядок меняется, а вкладки остальных проектов не должны сдвинуться
/// ни на позицию — иначе поедут счётчики и возврат к соседнему проекту.
/// </para>
/// </summary>
public sealed class TabReorderTests
{
    private static readonly Guid FirstProject = Guid.NewGuid();
    private static readonly Guid SecondProject = Guid.NewGuid();

    [Fact]
    public void Dragging_a_tab_to_the_left_moves_it_to_the_target_gap()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        Assert.True(strip.Reorder(tabs[2], 0));

        Assert.Equal(["c", "a", "b"], Titles(strip));
    }

    [Fact]
    public void Dragging_a_tab_to_the_right_accounts_for_the_gap_it_leaves_behind()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        // Промежуток 2 — «между b и c», и считается он по полосе вместе с самой вкладкой a.
        // Без поправки на уход a с первого места она встала бы за c.
        Assert.True(strip.Reorder(tabs[0], 2));

        Assert.Equal(["b", "a", "c"], Titles(strip));
    }

    [Fact]
    public void A_tab_dropped_past_the_last_gap_goes_to_the_end_of_the_strip()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        Assert.True(strip.Reorder(tabs[0], 3));

        Assert.Equal(["b", "c", "a"], Titles(strip));
    }

    [Fact]
    public void A_tab_dropped_beyond_the_edges_of_the_strip_lands_at_the_edge()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        // Бросок за край полосы не должен ни падать, ни терять перестановку.
        Assert.True(strip.Reorder(tabs[1], 99));
        Assert.Equal(["a", "c", "b"], Titles(strip));

        Assert.True(strip.Reorder(tabs[1], -5));
        Assert.Equal(["b", "a", "c"], Titles(strip));
    }

    [Fact]
    public void Dropping_a_tab_where_it_already_is_changes_nothing()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        // Промежуток перед собой и промежуток за собой — одно и то же место.
        Assert.False(strip.Reorder(tabs[1], 1));
        Assert.False(strip.Reorder(tabs[1], 2));

        Assert.Equal(["a", "b", "c"], Titles(strip));
    }

    [Fact]
    public void Reordering_a_single_tab_is_harmless()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a");
        strip.ShowProject(FirstProject);

        Assert.False(strip.Reorder(tabs[0], 0));
        Assert.False(strip.Reorder(tabs[0], 1));

        Assert.Equal(["a"], Titles(strip));
    }

    [Fact]
    public void A_tab_of_another_project_cannot_be_dropped_into_the_strip()
    {
        var strip = new TabStripViewModel();
        var mine = Open(strip, FirstProject, "a", "b");
        var foreign = Open(strip, SecondProject, "x");
        strip.ShowProject(FirstProject);

        Assert.False(strip.Reorder(foreign[0], 0));

        Assert.Equal(["a", "b"], Titles(strip));
        Assert.Equal(["a", "b", "x"], strip.AllTabs.Select(tab => tab.ShortTitle));
        Assert.Equal(2, mine.Count);
    }

    [Fact]
    public void Tabs_of_other_projects_do_not_move_when_the_strip_is_reordered()
    {
        var strip = new TabStripViewModel();

        // Вкладки двух проектов вперемешку: так они и лежат в общем списке.
        var a = Add(strip, FirstProject, "a");
        var x = Add(strip, SecondProject, "x");
        var b = Add(strip, FirstProject, "b");
        var y = Add(strip, SecondProject, "y");
        var c = Add(strip, FirstProject, "c");
        strip.ShowProject(FirstProject);

        Assert.True(strip.Reorder(c, 0));

        // Видимый порядок переставлен, а чужие вкладки остались на своих местах в общем
        // списке — между ними по-прежнему стоят вкладки первого проекта в новом порядке.
        Assert.Equal(["c", "a", "b"], Titles(strip));
        Assert.Equal([c, x, a, y, b], strip.AllTabs);
    }

    [Fact]
    public void The_strip_is_still_the_all_tabs_list_filtered_by_project()
    {
        var strip = new TabStripViewModel();
        Add(strip, FirstProject, "a");
        Add(strip, SecondProject, "x");
        var b = Add(strip, FirstProject, "b");
        Add(strip, SecondProject, "y");
        Add(strip, FirstProject, "c");
        strip.ShowProject(FirstProject);

        strip.Reorder(b, 3);

        Assert.Equal(Filtered(strip, FirstProject), strip.Tabs);

        strip.ShowProject(SecondProject);
        Assert.Equal(Filtered(strip, SecondProject), strip.Tabs);
    }

    [Fact]
    public void The_new_order_survives_a_round_trip_through_another_project()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        Open(strip, SecondProject, "x");
        strip.ShowProject(FirstProject);

        strip.Reorder(tabs[2], 0);

        // Переключение проекта собирает полосу заново фильтрацией общего списка: если бы
        // перестановка жила только в видимом списке, порядок здесь бы и откатился.
        strip.ShowProject(SecondProject);
        strip.ShowProject(FirstProject);

        Assert.Equal(["c", "a", "b"], Titles(strip));
    }

    [Fact]
    public void Ctrl_number_follows_the_new_order()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        strip.Reorder(tabs[2], 0);

        Assert.Same(tabs[2], strip.ByNumber(1));
        Assert.Same(tabs[0], strip.ByNumber(2));
        Assert.Same(tabs[1], strip.ByNumber(3));
        Assert.Null(strip.ByNumber(4));
    }

    [Fact]
    public void Ctrl_tab_follows_the_new_order()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);
        strip.SetActive(tabs[0]);

        strip.Reorder(tabs[0], 3);

        // Вкладка уехала в конец полосы, поэтому следующая по кругу — самая левая.
        Assert.Same(tabs[1], strip.Next());
        Assert.Same(tabs[2], strip.Previous());
    }

    [Fact]
    public void The_awaiting_input_counter_leads_to_the_leftmost_waiting_tab()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        tabs[1].State = TabState.AwaitingInput;
        tabs[2].State = TabState.AwaitingInput;
        Assert.Same(tabs[1], strip.FirstAwaitingInput());

        // После перестановки «первая ждущая» — та, что стоит левее в полосе, а не та,
        // что открыта раньше: порядок полосы задаёт пользователь.
        strip.Reorder(tabs[2], 0);

        Assert.Same(tabs[2], strip.FirstAwaitingInput());
        Assert.Equal(2, strip.AwaitingInputCount);
    }

    [Fact]
    public void Closing_a_tab_after_a_reorder_activates_the_neighbour_of_the_new_place()
    {
        var strip = new TabStripViewModel();
        var tabs = Open(strip, FirstProject, "a", "b", "c");
        strip.ShowProject(FirstProject);

        strip.Reorder(tabs[0], 3);
        strip.SetActive(tabs[0]);

        // a теперь последняя в полосе: закрытие уводит на соседнюю слева, то есть на c.
        Assert.Same(tabs[2], strip.Remove(tabs[0]));
        Assert.Equal(["b", "c"], Titles(strip));
    }

    private static List<TabViewModel> Open(TabStripViewModel strip, Guid projectId, params string[] titles) =>
        [.. titles.Select(title => Add(strip, projectId, title))];

    private static TabViewModel Add(TabStripViewModel strip, Guid projectId, string title)
    {
        var tab = new TabViewModel(TerminalId.New(), projectId, "проект") { ShortTitle = title };
        strip.Add(tab);
        return tab;
    }

    private static IEnumerable<string> Titles(TabStripViewModel strip) =>
        strip.Tabs.Select(tab => tab.ShortTitle);

    private static IEnumerable<TabViewModel> Filtered(TabStripViewModel strip, Guid projectId) =>
        strip.AllTabs.Where(tab => tab.ProjectId == projectId);
}
