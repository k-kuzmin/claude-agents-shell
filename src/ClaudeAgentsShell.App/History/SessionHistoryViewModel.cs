using System.Windows.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.History;

/// <summary>
/// Окно истории сессий одного проекта (раздел 6.4 ТЗ; фильтра по проектам нет — решение
/// пользователя): поиск по первому сообщению, список от свежих к старым, выбор стрелками,
/// <c>Enter</c> и <c>Esc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Загрузка асинхронная: окно показывается сразу, чтение уходит в пул потоков, а результат
/// применяется в потоке интерфейса через <see cref="IUiDispatcher"/>. Сбой чтения не роняет
/// окно — список пуст, заглушка говорит, что историю прочитать не удалось.
/// </para>
/// <para>
/// Пока окно открыто, держится подписка <see cref="ISessionHistoryWatcher"/> на проект;
/// по событию история перечитывается, выделение сохраняется по идентификатору сессии.
/// <see cref="Dispose"/> снимает подписку и гасит работу, уже поставленную в очередь диспетчера.
/// </para>
/// <para>
/// Все методы и свойства, кроме колбэка наблюдателя, вызываются из потока интерфейса.
/// </para>
/// </remarks>
public sealed class SessionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly ISessionHistoryReader _reader;
    private readonly ISessionHistoryWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly SessionHistoryProject _project;
    private readonly IReadOnlySet<string> _openSessionIds;
    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<SessionSummary>? _sessions;
    private bool _readFailed;

    // Чтение идёт сейчас / нужно перечитать сразу после него: под непрерывную запись сессии
    // наблюдатель зовёт примерно раз в 200 мс, и чтения не должны перекрываться.
    private bool _reading;
    private bool _rereadPending;

    private IDisposable? _subscription;
    private string _searchText = string.Empty;
    private IReadOnlyList<SessionHistoryRowViewModel> _rows = [];
    private SessionHistoryRowViewModel? _selectedRow;
    private bool _started;
    private bool _disposed;

    /// <inheritdoc cref="SessionHistoryViewModel" />
    /// <param name="request">Проект и открытые сейчас сессии.</param>
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
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.OpenSessionIds);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reader = reader;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _project = request.Project;
        _openSessionIds = request.OpenSessionIds;

        AcceptCommand = new RelayCommand(_ => Accept(), _ => _selectedRow is not null);
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    /// <summary>Окно просит закрыть себя. <see cref="Result"/> уже выставлен.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Имя проекта — в заголовке окна.</summary>
    public string ProjectName => _project.Name;

    /// <summary>Заголовок окна в системе: панель задач, Alt+Tab, экранный диктор.</summary>
    public string WindowTitle => $"История сессий — {_project.Name}";

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
        private set
        {
            var hadSelection = _selectedRow is not null;
            if (SetProperty(ref _selectedRow, value) && hadSelection != value is not null)
            {
                // Выделение появляется после чтения, без жеста пользователя, а сам WPF
                // доступность команд перепроверяет только по жестам.
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Щелчок по подсказке «Enter» в подвале или по плашке выделенной строки — то же, что
    /// <c>Enter</c>. Без выделения недоступна.
    /// </summary>
    public ICommand AcceptCommand { get; }

    /// <summary>Щелчок по подсказке «Esc» — то же, что <c>Esc</c>.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Идёт чтение истории.</summary>
    public bool IsLoading => _reading;

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

            if (_sessions is null && !_readFailed)
            {
                return IsLoading ? "Загружаем историю…" : null;
            }

            if (_readFailed)
            {
                return "Историю прочитать не удалось";
            }

            return _sessions is { Count: > 0 } ? "Ничего не найдено" : "В этом проекте ещё нет сессий";
        }
    }

    /// <summary>Правая часть подвала: загрузка, сбой чтения либо источник истории.</summary>
    public string FooterStatus =>
        IsLoading ? "загрузка…"
        : _readFailed ? "не удалось прочитать"
        : "~/.claude/projects";

    /// <summary>Выбор пользователя; <c>null</c> — окно закрыли без выбора.</summary>
    public SessionHistoryChoice? Result { get; private set; }

    /// <summary>
    /// Работа, запущенная последним событием наблюдателя или отложенным перечитыванием.
    /// Нужна тестам, чтобы дождаться чтения без опроса.
    /// </summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Подписывается на изменения истории проекта и читает её. Вызывается один раз,
    /// когда окно показано.
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
        Subscribe();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await RefreshAsync(linked.Token).ConfigureAwait(false);
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

        Result = new SessionHistoryChoice(_project.Id, row.SessionId);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>Esc</c>: закрыть без выбора.</summary>
    public void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снимает подписку наблюдателя и гасит незавершённое чтение.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _subscription?.Dispose();
        _subscription = null;
        _lifetime.Dispose();
    }

    private void Subscribe()
    {
        try
        {
            // Колбэк приходит из пула потоков: всё дальнейшее — в потоке интерфейса.
            _subscription = _watcher.Watch(_project.WorkingDirectory, () => _dispatcher.Post(OnHistoryChanged));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Без живого обновления окно остаётся полезным: список прочитан один раз.
        }
    }

    private void OnHistoryChanged()
    {
        // Событие могло лежать в очереди диспетчера, пока окно закрывали.
        if (_disposed)
        {
            return;
        }

        PendingRefresh = RefreshAsync(_lifetime.Token);
    }

    // Стартует в потоке интерфейса; результат применяется им же через диспетчер.
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_reading)
        {
            // Чтение уже идёт — второе параллельно не запускаем, а перечитаем после него:
            // текущее могло взять снимок раньше изменения.
            _rereadPending = true;
            return;
        }

        _reading = true;
        RaiseStatus();

        IReadOnlyList<SessionSummary>? sessions = null;
        var failed = false;
        try
        {
            // Пул, а не поток интерфейса: перечисление каталога и попадания в кэш у читателя
            // синхронные, и сотня файлов на медленном диске иначе задержала бы показ окна.
            sessions = await Task.Run(
                    () => _reader.ReadAsync(_project.WorkingDirectory, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Окно закрыто или загрузку отменили — применять нечего.
        }
        catch (Exception)
        {
            // Любой сбой чтения деградирует до пустого списка, окно живёт дальше.
            failed = true;
        }

        _dispatcher.Post(() => Apply(sessions, failed));
    }

    private void Apply(IReadOnlyList<SessionSummary>? sessions, bool failed)
    {
        if (_disposed)
        {
            return;
        }

        _reading = false;

        if (sessions is not null || failed)
        {
            _sessions = sessions ?? [];
            _readFailed = failed;
            Rebuild();
        }
        else
        {
            RaiseStatus();
        }

        if (_rereadPending)
        {
            _rereadPending = false;
            PendingRefresh = RefreshAsync(_lifetime.Token);
        }
    }

    private void Rebuild()
    {
        var keepSession = _selectedRow?.SessionId;

        var nowUtc = _timeProvider.GetUtcNow();
        var zone = _timeProvider.LocalTimeZone;
        var search = _searchText.Trim();

        var rows = new List<SessionHistoryRowViewModel>();
        foreach (var session in _sessions ?? [])
        {
            var row = CreateRow(session, nowUtc, zone);
            if (search.Length == 0 || row.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(row);
            }
        }

        // Порядок — с той точностью, с какой время показано: до минуты, а внутри минуты по
        // идентификатору. По точному времени несколько пишущих сессий менялись бы местами
        // на каждом перечитывании, и список мигал бы, хотя на экране ничего не поменялось.
        rows.Sort(static (a, b) =>
        {
            var byMinute = MinuteOf(b.ModifiedUtc).CompareTo(MinuteOf(a.ModifiedUtc));
            return byMinute != 0 ? byMinute : string.CompareOrdinal(a.SessionId, b.SessionId);
        });

        // Идущая сессия пишет транскрипт непрерывно, и наблюдатель будит окно раз в ~200 мс.
        // Если на экране ничего не поменялось, список не подменяется: иначе ListBox
        // пересоздавал бы строки, сбрасывал прокрутку и мигал подсветкой.
        if (!LooksSame(_rows, rows))
        {
            Rows = rows;
            SelectedRow = rows.FirstOrDefault(r => r.SessionId == keepSession) ?? rows.FirstOrDefault();
        }

        RaiseStatus();
    }

    private static long MinuteOf(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return ticks - (ticks % TimeSpan.TicksPerMinute);
    }

    private SessionHistoryRowViewModel CreateRow(SessionSummary session, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var title = SessionHistoryFormat.SingleLine(session.Title);
        var isTitleMissing = title is null;

        // Заголовка нет — деградация до «имя файла и дата» (раздел 7 CLAUDE.md).
        title ??= System.IO.Path.GetFileName(session.TranscriptPath) is { Length: > 0 } fileName
            ? fileName
            : session.SessionId;

        var parts = new List<string>(3) { SessionHistoryFormat.Date(session.ModifiedUtc, nowUtc, zone) };
        if (!string.IsNullOrWhiteSpace(session.Branch))
        {
            parts.Add(session.Branch);
        }

        parts.Add(SessionHistoryFormat.ShortId(session.SessionId));

        return new SessionHistoryRowViewModel(
            session.SessionId,
            title,
            isTitleMissing,
            string.Join(" · ", parts),
            _openSessionIds.Contains(session.SessionId),
            session.ModifiedUtc);
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
            if (a.IsTitleMissing != b.IsTitleMissing
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
}
