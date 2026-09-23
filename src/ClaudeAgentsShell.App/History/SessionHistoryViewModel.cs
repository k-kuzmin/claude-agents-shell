using System.Windows.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.History;

/// <summary>
/// Окно истории сессий (раздел 6.4 ТЗ): фильтр по проектам, поиск по первому сообщению,
/// список от свежих к старым, выбор стрелками, <c>Enter</c> и <c>Esc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Загрузка асинхронная: окно показывается сразу, чтение уходит в пул потоков, а результат
/// применяется в потоке интерфейса через <see cref="IUiDispatcher"/>. Сбой чтения одного
/// проекта не роняет окно — проект показывается пустым с пометкой в подвале.
/// </para>
/// <para>
/// Пока окно открыто, на каждый показываемый проект держится подписка
/// <see cref="ISessionHistoryWatcher"/>; по событию проект перечитывается, выделение
/// сохраняется по идентификатору сессии. <see cref="Dispose"/> снимает все подписки и
/// гасит работу, уже поставленную в очередь диспетчера.
/// </para>
/// <para>
/// Все методы и свойства, кроме колбэков наблюдателя, вызываются из потока интерфейса.
/// </para>
/// </remarks>
public sealed class SessionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly ISessionHistoryReader _reader;
    private readonly ISessionHistoryWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<SessionHistoryProject> _projects;
    private readonly IReadOnlySet<string> _openSessionIds;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Dictionary<Guid, ProjectHistory> _histories = [];
    // Проекты, которые читаются сейчас, и те, что надо перечитать сразу после текущего
    // чтения: под непрерывную запись сессии наблюдатель зовёт примерно раз в 200 мс, и
    // чтения одного проекта не должны перекрываться.
    private readonly HashSet<Guid> _reading = [];
    private readonly HashSet<Guid> _rereadPending = [];
    private readonly Dictionary<Guid, IDisposable> _subscriptions = [];

    private SessionHistoryFilterViewModel _selectedFilter;
    private string _searchText = string.Empty;
    private IReadOnlyList<SessionHistoryRowViewModel> _rows = [];
    private SessionHistoryRowViewModel? _selectedRow;
    private bool _started;
    private bool _disposed;

    /// <inheritdoc cref="SessionHistoryViewModel" />
    /// <param name="request">Проекты фильтра, стартовый проект и открытые сейчас сессии.</param>
    /// <param name="reader">Чтение истории рабочего каталога.</param>
    /// <param name="watcher">Уведомления об изменении истории.</param>
    /// <param name="dispatcher">Перевод результатов чтения в поток интерфейса.</param>
    /// <param name="timeProvider">Текущее время и часовой пояс — для «сегодня» и «вчера».</param>
    public SessionHistoryViewModel(
        SessionHistoryRequest request,
        ISessionHistoryReader reader,
        ISessionHistoryWatcher watcher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Projects);
        ArgumentNullException.ThrowIfNull(request.OpenSessionIds);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reader = reader;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _projects = request.Projects;
        _openSessionIds = request.OpenSessionIds;

        ProjectFilters = request.Projects
            .Select(static p => new SessionHistoryFilterViewModel(p.Id, p.Name))
            .ToArray();
        AllProjectsFilter = new SessionHistoryFilterViewModel(null, "все проекты");

        // Проекта строки в списке может не оказаться (убрали, пока окно открывалось) —
        // тогда честнее показать всё, чем пустоту.
        _selectedFilter = ProjectFilters.FirstOrDefault(f => f.ProjectId == request.InitialProjectId)
                          ?? AllProjectsFilter;
        _selectedFilter.IsSelected = true;

        SelectFilterCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is SessionHistoryFilterViewModel filter)
                {
                    SelectFilter(filter);
                }
            });
    }

    /// <summary>Окно просит закрыть себя. <see cref="Result"/> уже выставлен.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Фильтры по отдельным проектам в порядке панели.</summary>
    public IReadOnlyList<SessionHistoryFilterViewModel> ProjectFilters { get; }

    /// <summary>Фильтр «все проекты».</summary>
    public SessionHistoryFilterViewModel AllProjectsFilter { get; }

    /// <summary>Выбранный фильтр.</summary>
    public SessionHistoryFilterViewModel SelectedFilter => _selectedFilter;

    /// <summary>Выбор фильтра; параметр — <see cref="SessionHistoryFilterViewModel"/>.</summary>
    public ICommand SelectFilterCommand { get; }

    /// <summary>Строка поиска по первому сообщению; список фильтруется по мере ввода, без учёта регистра.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Видимые строки: от свежих к старым.</summary>
    public IReadOnlyList<SessionHistoryRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>Выделенная строка; <c>null</c> — список пуст.</summary>
    public SessionHistoryRowViewModel? SelectedRow
    {
        get => _selectedRow;
        private set => SetProperty(ref _selectedRow, value);
    }

    /// <summary>Идёт чтение истории хотя бы одного показываемого проекта.</summary>
    public bool IsLoading => ShownProjects().Any(p => _reading.Contains(p.Id));

    /// <summary>
    /// Текст на месте пустого списка; <c>null</c>, когда строки есть. Пустая история —
    /// не ошибка (раздел 8 ТЗ), и заглушка говорит именно это.
    /// </summary>
    public string? EmptyText
    {
        get
        {
            if (Rows.Count > 0)
            {
                return null;
            }

            var shown = ShownProjects().ToArray();
            var loaded = shown.Where(p => _histories.ContainsKey(p.Id)).ToArray();

            if (loaded.Length < shown.Length && IsLoading)
            {
                return "Загружаем историю…";
            }

            if (loaded.Length > 0 && loaded.All(p => _histories[p.Id].Failed))
            {
                return "Историю прочитать не удалось";
            }

            if (loaded.Any(p => _histories[p.Id].Sessions.Count > 0))
            {
                return "Ничего не найдено";
            }

            return _selectedFilter.IsAllProjects ? "Сессий пока нет" : "В этом проекте ещё нет сессий";
        }
    }

    /// <summary>Правая часть подвала: загрузка, частичный сбой чтения либо источник истории.</summary>
    public string FooterStatus
    {
        get
        {
            if (IsLoading)
            {
                return "загрузка…";
            }

            return ShownProjects().Any(p => _histories.TryGetValue(p.Id, out var h) && h.Failed)
                ? "не всё удалось прочитать"
                : "~/.claude/projects";
        }
    }

    /// <summary>Выбор пользователя; <c>null</c> — окно закрыли без выбора.</summary>
    public SessionHistoryChoice? Result { get; private set; }

    /// <summary>
    /// Работа, запущенная последним событием наблюдателя или сменой фильтра. Нужна тестам,
    /// чтобы дождаться чтения без опроса.
    /// </summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Подписывается на изменения показываемых проектов и читает их историю.
    /// Вызывается один раз, когда окно показано.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Окно могли закрыть раньше, чем оно успело загрузиться, — это не ошибка.
        if (_disposed || _started)
        {
            return;
        }

        _started = true;
        cancellationToken.ThrowIfCancellationRequested();

        // Подписка раньше чтения: изменение между чтением и подпиской иначе потерялось бы.
        UpdateSubscriptions();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await RefreshShownAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Переключает фильтр: подписки перестраиваются под показываемые проекты.</summary>
    public void SelectFilter(SessionHistoryFilterViewModel filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (_disposed || ReferenceEquals(filter, _selectedFilter))
        {
            return;
        }

        if (!ReferenceEquals(filter, AllProjectsFilter) && !ProjectFilters.Contains(filter))
        {
            throw new ArgumentException("Фильтр не из этого окна.", nameof(filter));
        }

        _selectedFilter.IsSelected = false;
        _selectedFilter = filter;
        filter.IsSelected = true;
        Raise(nameof(SelectedFilter));

        // Уже прочитанное показывается сразу, свежее доезжает следом.
        Rebuild();

        if (_started)
        {
            UpdateSubscriptions();
            PendingRefresh = RefreshShownAsync(_lifetime.Token);
        }
    }

    /// <summary>Выделяет строку — щелчок мышью.</summary>
    public void Select(SessionHistoryRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Rows.Contains(row))
        {
            SelectedRow = row;
        }
    }

    /// <summary>Сдвигает выделение на <paramref name="delta"/> строк, не выходя за края списка.</summary>
    public void MoveSelection(int delta)
    {
        var rows = Rows;
        if (rows.Count == 0)
        {
            return;
        }

        var current = _selectedRow is null ? -1 : IndexOf(rows, _selectedRow);
        var next = current < 0
            ? (delta >= 0 ? 0 : rows.Count - 1)
            : Math.Clamp(current + delta, 0, rows.Count - 1);

        SelectedRow = rows[next];
    }

    /// <summary><c>Enter</c> или двойной щелчок: возвращает выделенную сессию. Без выделения — ничего.</summary>
    public void Accept()
    {
        if (_selectedRow is not { } row)
        {
            return;
        }

        Result = new SessionHistoryChoice(row.ProjectId, row.SessionId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>Esc</c>: закрыть без выбора.</summary>
    public void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снимает подписки наблюдателя и гасит незавершённые чтения.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _lifetime.Dispose();
    }

    private IEnumerable<SessionHistoryProject> ShownProjects() =>
        _selectedFilter.ProjectId is { } id
            ? _projects.Where(p => p.Id == id)
            : _projects;

    private void UpdateSubscriptions()
    {
        var shown = ShownProjects().ToArray();

        foreach (var id in _subscriptions.Keys.ToArray())
        {
            if (!shown.Any(p => p.Id == id))
            {
                _subscriptions[id].Dispose();
                _subscriptions.Remove(id);
            }
        }

        foreach (var project in shown)
        {
            if (_subscriptions.ContainsKey(project.Id))
            {
                continue;
            }

            try
            {
                // Колбэк приходит из пула потоков: всё дальнейшее — в потоке интерфейса.
                _subscriptions[project.Id] = _watcher.Watch(
                    project.WorkingDirectory,
                    () => _dispatcher.Post(() => OnHistoryChanged(project)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Без живого обновления окно остаётся полезным: список прочитан один раз.
            }
        }
    }

    private void OnHistoryChanged(SessionHistoryProject project)
    {
        // Событие могло лежать в очереди диспетчера, пока окно закрывали или меняли фильтр.
        if (_disposed || !_subscriptions.ContainsKey(project.Id))
        {
            return;
        }

        PendingRefresh = RefreshProjectAsync(project, _lifetime.Token);
    }

    private Task RefreshShownAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(ShownProjects().Select(p => RefreshProjectAsync(p, cancellationToken)).ToArray());

    // Стартует в потоке интерфейса; результат применяется им же через диспетчер.
    private async Task RefreshProjectAsync(SessionHistoryProject project, CancellationToken cancellationToken)
    {
        if (!_reading.Add(project.Id))
        {
            // Чтение уже идёт — второе параллельно не запускаем, а перечитаем после него:
            // текущее могло взять снимок раньше изменения.
            _rereadPending.Add(project.Id);
            return;
        }

        RaiseStatus();

        IReadOnlyList<SessionSummary>? sessions = null;
        var failed = false;
        try
        {
            // Пул, а не поток интерфейса: перечисление каталога и попадания в кэш у читателя
            // синхронные, и сотня файлов на медленном диске иначе задержала бы показ окна.
            sessions = await Task.Run(
                    () => _reader.ReadAsync(project.WorkingDirectory, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Окно закрыто или загрузку отменили — применять нечего.
        }
        catch (Exception)
        {
            // Любой сбой чтения деградирует до пустого списка проекта, окно живёт дальше.
            failed = true;
        }

        _dispatcher.Post(() => Apply(project, sessions, failed));
    }

    private void Apply(SessionHistoryProject project, IReadOnlyList<SessionSummary>? sessions, bool failed)
    {
        if (_disposed)
        {
            return;
        }

        _reading.Remove(project.Id);

        if (sessions is not null || failed)
        {
            _histories[project.Id] = new ProjectHistory(sessions ?? [], failed);
            Rebuild();
        }
        else
        {
            RaiseStatus();
        }

        // Пока шло чтение, пришло изменение или сменился фильтр — перечитываем, если проект
        // ещё показывается. Иначе его прочтёт следующая смена фильтра.
        if (_rereadPending.Remove(project.Id) && ShownProjects().Any(p => p.Id == project.Id))
        {
            PendingRefresh = RefreshProjectAsync(project, _lifetime.Token);
        }
    }

    private void Rebuild()
    {
        var keepProject = _selectedRow?.ProjectId;
        var keepSession = _selectedRow?.SessionId;

        var nowUtc = _timeProvider.GetUtcNow();
        var zone = _timeProvider.LocalTimeZone;
        var withProjectName = _selectedFilter.IsAllProjects;
        var search = _searchText.Trim();

        var rows = new List<SessionHistoryRowViewModel>();
        foreach (var project in ShownProjects())
        {
            if (!_histories.TryGetValue(project.Id, out var history))
            {
                continue;
            }

            foreach (var session in history.Sessions)
            {
                var row = CreateRow(project, session, nowUtc, zone, withProjectName);
                if (search.Length == 0 || row.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(row);
                }
            }
        }

        // Общий список «все проекты» — единой лентой по дате, а не проект за проектом.
        rows.Sort(static (a, b) =>
        {
            var byDate = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
            return byDate != 0 ? byDate : string.CompareOrdinal(a.SessionId, b.SessionId);
        });

        // Идущая сессия пишет транскрипт непрерывно, и наблюдатель будит окно раз в ~200 мс.
        // Если на экране ничего не поменялось (время в строке — с точностью до минуты),
        // список не подменяется: иначе ListBox пересоздавал бы строки, сбрасывал прокрутку
        // и мигал подсветкой.
        if (!LooksSame(_rows, rows))
        {
            Rows = rows;
            SelectedRow = rows.FirstOrDefault(r => r.ProjectId == keepProject && r.SessionId == keepSession)
                          ?? rows.FirstOrDefault();
        }

        RaiseStatus();
    }

    private static bool LooksSame(IReadOnlyList<SessionHistoryRowViewModel> current, List<SessionHistoryRowViewModel> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < next.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a.ProjectId != b.ProjectId
                || a.IsTitleMissing != b.IsTitleMissing
                || a.IsOpen != b.IsOpen
                || !string.Equals(a.SessionId, b.SessionId, StringComparison.Ordinal)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
                || !string.Equals(a.Details, b.Details, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private SessionHistoryRowViewModel CreateRow(
        SessionHistoryProject project,
        SessionSummary session,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        bool withProjectName)
    {
        var title = SessionHistoryFormat.SingleLine(session.Title);
        var isTitleMissing = title is null;

        // Заголовка нет — деградация до «имя файла и дата» (раздел 7 CLAUDE.md).
        title ??= System.IO.Path.GetFileName(session.TranscriptPath) is { Length: > 0 } fileName
            ? fileName
            : session.SessionId;

        var parts = new List<string>(4);
        if (withProjectName)
        {
            parts.Add(project.Name);
        }

        parts.Add(SessionHistoryFormat.Date(session.ModifiedUtc, nowUtc, zone));
        if (!string.IsNullOrWhiteSpace(session.Branch))
        {
            parts.Add(session.Branch);
        }

        parts.Add(SessionHistoryFormat.ShortId(session.SessionId));

        return new SessionHistoryRowViewModel(
            project.Id,
            session.SessionId,
            title,
            isTitleMissing,
            string.Join(" · ", parts),
            _openSessionIds.Contains(session.SessionId),
            session.ModifiedUtc);
    }

    private void RaiseStatus()
    {
        Raise(nameof(IsLoading));
        Raise(nameof(EmptyText));
        Raise(nameof(FooterStatus));
    }

    private static int IndexOf(IReadOnlyList<SessionHistoryRowViewModel> rows, SessionHistoryRowViewModel row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record ProjectHistory(IReadOnlyList<SessionSummary> Sessions, bool Failed);
}
