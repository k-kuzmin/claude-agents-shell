using ClaudeAgentsShell.App.History;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

public sealed class HistoryViewModelTests
{
    private const string CoreDir = @"C:\src\core-api";
    private const string BuildDir = @"C:\src\build-pipeline";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

    private static readonly SessionHistoryProject Core = new(Guid.NewGuid(), "core-api", CoreDir);
    private static readonly SessionHistoryProject Build = new(Guid.NewGuid(), "build-pipeline", BuildDir);

    private readonly ScriptedHistoryReader _reader = new();
    private readonly RecordingHistoryWatcher _watcher = new();

    [Fact]
    public async Task Filter_starts_on_initial_project_and_shows_only_its_sessions()
    {
        _reader.Set(CoreDir, Session("0d41f2a7-core", "добавь репозиторий", Now.AddHours(-1)));
        _reader.Set(BuildDir, Session("7be0c193-build", "почини сборку", Now.AddMinutes(-5)));
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Same(vm.ProjectFilters[0], vm.SelectedFilter);
        Assert.True(vm.ProjectFilters[0].IsSelected);
        Assert.False(vm.AllProjectsFilter.IsSelected);
        Assert.Equal(["0d41f2a7-core"], vm.Rows.Select(r => r.SessionId));
        Assert.Equal([CoreDir], _watcher.LiveDirectories);
    }

    [Fact]
    public async Task Unknown_initial_project_falls_back_to_all_projects()
    {
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        _reader.Set(BuildDir, Session("b", "вторая", Now.AddHours(-2)));
        using var vm = Create(Guid.NewGuid());

        await vm.LoadAsync(CancellationToken.None);

        Assert.Same(vm.AllProjectsFilter, vm.SelectedFilter);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task All_projects_is_one_list_by_date_with_project_names()
    {
        _reader.Set(CoreDir,
            Session("core-old", "старая", Now.AddDays(-3)),
            Session("core-new", "свежая", Now.AddMinutes(-1)));
        _reader.Set(BuildDir, Session("build-mid", "средняя", Now.AddHours(-2)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);

        vm.SelectFilter(vm.AllProjectsFilter);
        await vm.PendingRefresh;

        Assert.Equal(["core-new", "build-mid", "core-old"], vm.Rows.Select(r => r.SessionId));
        Assert.StartsWith("build-pipeline · ", vm.Rows[1].Details, StringComparison.Ordinal);
        Assert.StartsWith("core-api · ", vm.Rows[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_project_rows_have_no_project_name_and_no_message_count()
    {
        _reader.Set(CoreDir, Session("0d41f2a7-1111-2222", "задача", new DateTimeOffset(2026, 9, 23, 14, 36, 0, TimeSpan.Zero), branch: "feat/orders", messages: 18));
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("сегодня 14:36 · feat/orders · 0d41f2a7", vm.Rows[0].Details);
    }

    [Fact]
    public async Task Row_without_branch_skips_it()
    {
        _reader.Set(CoreDir, Session("0d41f2a7-1111", "задача", new DateTimeOffset(2026, 9, 22, 9, 5, 0, TimeSpan.Zero)));
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("вчера 09:05 · 0d41f2a7", vm.Rows[0].Details);
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_live()
    {
        _reader.Set(CoreDir,
            Session("a", "Почини ФЛАКАЮЩИЙ тест", Now.AddHours(-1)),
            Session("b", "разбери логи воркера", Now.AddHours(-2)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);

        vm.SearchText = "флакающий";
        Assert.Equal(["a"], vm.Rows.Select(r => r.SessionId));

        vm.SearchText = "ЛОГИ";
        Assert.Equal(["b"], vm.Rows.Select(r => r.SessionId));
        Assert.Equal("b", vm.SelectedRow?.SessionId);

        vm.SearchText = "нет такого";
        Assert.Empty(vm.Rows);
        Assert.Null(vm.SelectedRow);
        Assert.Equal("Ничего не найдено", vm.EmptyText);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Search_keeps_selection_when_the_row_stays_visible()
    {
        _reader.Set(CoreDir,
            Session("a", "тест один", Now.AddHours(-1)),
            Session("b", "тест два", Now.AddHours(-2)),
            Session("c", "другое", Now.AddHours(-3)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        vm.MoveSelection(1);

        vm.SearchText = "тест";

        Assert.Equal("b", vm.SelectedRow?.SessionId);
    }

    [Fact]
    public async Task Open_sessions_are_marked()
    {
        _reader.Set(CoreDir, Session("open", "открытая", Now.AddHours(-1)), Session("closed", "закрытая", Now.AddHours(-2)));
        using var vm = Create(Core.Id, openSessionIds: new HashSet<string> { "open" });

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.Rows.Single(r => r.SessionId == "open").IsOpen);
        Assert.False(vm.Rows.Single(r => r.SessionId == "closed").IsOpen);
    }

    [Fact]
    public async Task Missing_title_degrades_to_file_name()
    {
        _reader.Set(CoreDir, Session("c40a5f2e-9999", title: null, Now.AddHours(-1)));
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsTitleMissing);
        Assert.Equal("c40a5f2e-9999.jsonl", row.Title);
    }

    [Fact]
    public async Task Multiline_title_is_shown_as_its_first_line()
    {
        _reader.Set(CoreDir, Session("a", "  первая   строка\n\nвторая", Now.AddHours(-1)));
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("первая строка", vm.Rows[0].Title);
        Assert.False(vm.Rows[0].IsTitleMissing);
    }

    [Fact]
    public async Task Enter_returns_selected_session()
    {
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)), Session("b", "вторая", Now.AddHours(-2)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.MoveSelection(1);
        vm.Accept();

        Assert.Equal(new SessionHistoryChoice(Core.Id, "b"), vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Enter_without_selection_does_nothing()
    {
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.Accept();

        Assert.Null(vm.Result);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task Esc_closes_with_null()
    {
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.Cancel();

        Assert.Null(vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Arrows_move_selection_within_bounds()
    {
        _reader.Set(CoreDir,
            Session("a", "1", Now.AddHours(-1)),
            Session("b", "2", Now.AddHours(-2)),
            Session("c", "3", Now.AddHours(-3)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("a", vm.SelectedRow?.SessionId);
        vm.MoveSelection(-1);
        Assert.Equal("a", vm.SelectedRow?.SessionId);
        vm.MoveSelection(1);
        vm.MoveSelection(1);
        vm.MoveSelection(1);
        Assert.Equal("c", vm.SelectedRow?.SessionId);
        vm.MoveSelection(-8);
        Assert.Equal("a", vm.SelectedRow?.SessionId);
    }

    [Fact]
    public async Task Watcher_change_rereads_on_ui_thread_and_keeps_selection()
    {
        var dispatcher = new QueuedUiDispatcher();
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)), Session("b", "вторая", Now.AddHours(-2)));
        using var vm = Create(Core.Id, dispatcher: dispatcher);
        await vm.LoadAsync(CancellationToken.None);
        dispatcher.Drain();
        vm.MoveSelection(1);
        Assert.Equal("b", vm.SelectedRow?.SessionId);

        // Новая сессия встаёт первой, выделение остаётся на «b».
        _reader.Set(CoreDir,
            Session("new", "новая", Now),
            Session("a", "первая", Now.AddHours(-1)),
            Session("b", "вторая", Now.AddHours(-2)));
        _watcher.Fire(CoreDir);
        Assert.Equal(2, vm.Rows.Count);

        dispatcher.Drain();
        await vm.PendingRefresh;
        Assert.Equal(2, vm.Rows.Count);
        dispatcher.Drain();

        Assert.Equal(["new", "a", "b"], vm.Rows.Select(r => r.SessionId));
        Assert.Equal("b", vm.SelectedRow?.SessionId);
    }

    [Fact]
    public async Task Watcher_change_without_visible_difference_keeps_the_same_rows()
    {
        var minute = new DateTimeOffset(2026, 9, 23, 17, 30, 5, TimeSpan.Zero);
        _reader.Set(CoreDir, Session("live", "идущая", minute), Session("b", "вторая", Now.AddHours(-2)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        vm.MoveSelection(1);
        var rowsBefore = vm.Rows;
        var selectedBefore = vm.SelectedRow;

        // Идущая сессия дописала транскрипт в ту же минуту: на экране ничего не изменилось.
        _reader.Set(CoreDir, Session("live", "идущая", minute.AddSeconds(40)), Session("b", "вторая", Now.AddHours(-2)));
        _watcher.Fire(CoreDir);
        await vm.PendingRefresh;

        Assert.Same(rowsBefore, vm.Rows);
        Assert.Same(selectedBefore, vm.SelectedRow);
    }

    [Fact]
    public async Task Watcher_change_selects_first_when_selected_session_disappears()
    {
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)), Session("b", "вторая", Now.AddHours(-2)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        vm.MoveSelection(1);

        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        _watcher.Fire(CoreDir);
        await vm.PendingRefresh;

        Assert.Equal("a", vm.SelectedRow?.SessionId);
    }

    [Fact]
    public async Task Changing_filter_moves_subscriptions_to_shown_projects()
    {
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal([CoreDir], _watcher.LiveDirectories);

        vm.SelectFilter(vm.AllProjectsFilter);
        await vm.PendingRefresh;
        Assert.Equal([BuildDir, CoreDir], _watcher.LiveDirectories.Order(StringComparer.Ordinal));

        vm.SelectFilter(vm.ProjectFilters[1]);
        await vm.PendingRefresh;
        Assert.Equal([BuildDir], _watcher.LiveDirectories);
        Assert.True(vm.ProjectFilters[1].IsSelected);
        Assert.False(vm.AllProjectsFilter.IsSelected);
    }

    [Fact]
    public async Task Filter_command_switches_filter()
    {
        _reader.Set(BuildDir, Session("b", "сборка", Now.AddHours(-1)));
        using var vm = Create(Core.Id);
        await vm.LoadAsync(CancellationToken.None);

        vm.SelectFilterCommand.Execute(vm.ProjectFilters[1]);
        await vm.PendingRefresh;

        Assert.Same(vm.ProjectFilters[1], vm.SelectedFilter);
        Assert.Equal(["b"], vm.Rows.Select(r => r.SessionId));
    }

    [Fact]
    public async Task Dispose_releases_subscriptions_and_ignores_queued_changes()
    {
        var dispatcher = new QueuedUiDispatcher();
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        var vm = Create(Guid.NewGuid(), dispatcher: dispatcher);
        await vm.LoadAsync(CancellationToken.None);
        dispatcher.Drain();
        var readsBefore = _reader.Reads;
        var rowsBefore = vm.Rows;

        _watcher.Fire(CoreDir);
        vm.Dispose();
        dispatcher.Drain();

        Assert.Empty(_watcher.LiveDirectories);
        Assert.Equal(2, _watcher.TotalSubscriptions);
        Assert.Equal(readsBefore, _reader.Reads);
        Assert.Same(rowsBefore, vm.Rows);
    }

    [Fact]
    public async Task Dispose_drops_read_that_finishes_after_close()
    {
        var dispatcher = new QueuedUiDispatcher();
        var gate = _reader.Hold(CoreDir);
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        var vm = Create(Core.Id, dispatcher: dispatcher);
        var load = vm.LoadAsync(CancellationToken.None);
        Assert.True(vm.IsLoading);

        vm.Dispose();
        gate.SetResult();
        await load;
        dispatcher.Drain();

        Assert.Empty(vm.Rows);
        Assert.Empty(_watcher.LiveDirectories);
    }

    [Fact]
    public async Task Loading_state_is_shown_until_read_completes()
    {
        var gate = _reader.Hold(CoreDir);
        _reader.Set(CoreDir, Session("a", "первая", Now.AddHours(-1)));
        using var vm = Create(Core.Id);

        var load = vm.LoadAsync(CancellationToken.None);
        Assert.True(vm.IsLoading);
        Assert.Equal("Загружаем историю…", vm.EmptyText);
        Assert.Equal("загрузка…", vm.FooterStatus);

        gate.SetResult();
        await load;

        Assert.False(vm.IsLoading);
        Assert.Null(vm.EmptyText);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Change_during_read_is_coalesced_into_one_reread_after_it()
    {
        var dispatcher = new QueuedUiDispatcher();
        var slow = _reader.Hold(CoreDir);
        _reader.Set(CoreDir, Session("old", "старая", Now.AddHours(-1)));
        using var vm = Create(Core.Id, dispatcher: dispatcher);
        var load = vm.LoadAsync(CancellationToken.None);

        // Чтение уходит в пул: ждём, пока оно возьмёт снимок «old» и повиснет на воротах.
        Assert.True(SpinWait.SpinUntil(() => _reader.Reads == 1, TimeSpan.FromSeconds(5)));

        // Три события, пока первое чтение висит: параллельных чтений нет.
        _reader.Set(CoreDir, Session("fresh", "свежая", Now));
        _watcher.Fire(CoreDir);
        _watcher.Fire(CoreDir);
        _watcher.Fire(CoreDir);
        dispatcher.Drain();

        Assert.Equal(1, _reader.Reads);

        slow.SetResult();
        await load;
        dispatcher.Drain();
        Assert.Equal(["old"], vm.Rows.Select(r => r.SessionId));
        Assert.True(vm.IsLoading);

        await vm.PendingRefresh;
        dispatcher.Drain();

        Assert.Equal(2, _reader.Reads);
        Assert.Equal(["fresh"], vm.Rows.Select(r => r.SessionId));
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Empty_history_is_a_placeholder_not_an_error()
    {
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Empty(vm.Rows);
        Assert.Equal("В этом проекте ещё нет сессий", vm.EmptyText);
        Assert.Equal("~/.claude/projects", vm.FooterStatus);
    }

    [Fact]
    public async Task Empty_history_of_all_projects_has_its_own_placeholder()
    {
        using var vm = Create(Guid.NewGuid());

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("Сессий пока нет", vm.EmptyText);
    }

    [Fact]
    public async Task Read_failure_keeps_window_alive()
    {
        _reader.Fail(CoreDir);
        using var vm = Create(Core.Id);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Empty(vm.Rows);
        Assert.Equal("Историю прочитать не удалось", vm.EmptyText);
        Assert.Equal("не всё удалось прочитать", vm.FooterStatus);
    }

    [Fact]
    public async Task Failure_of_one_project_does_not_hide_others()
    {
        _reader.Fail(CoreDir);
        _reader.Set(BuildDir, Session("b", "сборка", Now.AddHours(-1)));
        using var vm = Create(Guid.NewGuid());

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(["b"], vm.Rows.Select(r => r.SessionId));
        Assert.Equal("не всё удалось прочитать", vm.FooterStatus);
    }

    private SessionHistoryViewModel Create(
        Guid initialProjectId,
        IReadOnlySet<string>? openSessionIds = null,
        IUiDispatcher? dispatcher = null) =>
        new(
            new SessionHistoryRequest([Core, Build], initialProjectId, openSessionIds ?? new HashSet<string>()),
            _reader,
            _watcher,
            dispatcher ?? new InlineUiDispatcher(),
            new HistoryClock(Now, TimeZoneInfo.Utc));

    private static SessionSummary Session(
        string id,
        string? title,
        DateTimeOffset modified,
        string? branch = null,
        int? messages = null) =>
        new(id, $@"C:\Users\me\.claude\projects\slug\{id}.jsonl", modified, 100, title, messages, branch);
}
