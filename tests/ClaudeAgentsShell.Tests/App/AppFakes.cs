using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Список проектов в памяти вместо projects.json.</summary>
internal sealed class FakeProjectStore : IProjectStore
{
    private List<ProjectDefinition> _projects = [];

    public IReadOnlyList<ProjectDefinition> Saved { get; private set; } = [];

    public int SaveCount { get; private set; }

    /// <summary>Исключение, которым отвечает следующая запись.</summary>
    public Exception? SaveFailure { get; set; }

    public void Seed(params ProjectDefinition[] projects) => _projects = [.. projects];

    public Task<IReadOnlyList<ProjectDefinition>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProjectDefinition>>(_projects);

    public Task SaveAsync(IReadOnlyList<ProjectDefinition> projects, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            SaveFailure = null;
            return Task.FromException(failure);
        }

        Saved = [.. projects];
        _projects = [.. projects];
        SaveCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Ветка берётся из словаря, а не из .git/HEAD.</summary>
internal sealed class FakeGitBranchReader : IGitBranchReader
{
    private readonly Dictionary<string, string?> _branches = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, string? branch) => _branches[path] = branch;

    public Task<string?> ReadAsync(string workingDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(_branches.TryGetValue(workingDirectory, out var branch) ? branch : null);
}

/// <summary>Слежение за веткой без FileSystemWatcher.</summary>
internal sealed class FakeGitBranchWatcher : IGitBranchWatcher
{
    public List<string> Watched { get; } = [];

    public event EventHandler<GitBranchChangedEventArgs>? BranchChanged;

    public void Raise(string workingDirectory, string? branch) =>
        BranchChanged?.Invoke(this, new GitBranchChangedEventArgs(workingDirectory, branch));

    public Task WatchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        Watched.Add(workingDirectory);
        return Task.CompletedTask;
    }

    public void Unwatch(string workingDirectory) => Watched.Remove(workingDirectory);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Существующие каталоги задаются тестом.</summary>
internal sealed class FakeDirectoryProbe : IDirectoryProbe
{
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path) => _existing.Add(path);

    public void Remove(string path) => _existing.Remove(path);

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(_existing.Contains(path));
}

/// <summary>Диалог выбора папки: возвращает заранее назначенный путь.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? NextFolder { get; set; }

    public string? PickFolder(string title) => NextFolder;
}

/// <summary>Модальные окна: ответ задаётся тестом, показанное запоминается.</summary>
internal sealed class FakeUserPrompt : IUserPrompt
{
    public bool ConfirmResult { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public List<string> Errors { get; } = [];

    public bool Confirm(string title, string message)
    {
        Confirmations.Add(message);
        return ConfirmResult;
    }

    public void ShowError(string title, string message) => Errors.Add(message);
}

/// <summary>Поток интерфейса в тестах — текущий поток.</summary>
/// <remarks>
/// Годится только там, где отправитель уже находится в «потоке интерфейса»: колбэк исполняется
/// прямо на вызывающем потоке, и если отправить его из пула, обещание «только из потока
/// интерфейса» окажется нарушенным. Для кода, у которого работа уходит в пул, берите
/// <see cref="QueuedUiDispatcher"/>: он копит колбэки, а исполняет их тот поток, который зовёт
/// <see cref="QueuedUiDispatcher.Drain"/>.
/// </remarks>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Набор вкладок без ConPTY и страницы.</summary>
internal sealed class FakeTerminalWorkspace : ITerminalWorkspace
{
    private readonly List<TerminalId> _terminals = [];
    private int _counter;

    public event EventHandler<TerminalExitedEventArgs>? TerminalExited;

    public IReadOnlyList<TerminalId> Terminals => _terminals;

    /// <summary>Каталоги, в которых запрашивался запуск, по порядку.</summary>
    public List<string> OpenedDirectories { get; } = [];

    /// <summary>Вкладка, показанная на странице последней.</summary>
    public TerminalId? VisibleTerminal { get; private set; }

    /// <summary>Закрытые вкладки, по порядку.</summary>
    public List<TerminalId> Closed { get; } = [];

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Исключение, которым отвечает следующий запуск.</summary>
    public Exception? OpenFailure { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task<TerminalId> OpenAsync(ProjectDefinition project, SessionLaunch launch, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            OpenFailure = null;
            return Task.FromException<TerminalId>(failure);
        }

        OpenedDirectories.Add(project.Path);
        var id = new TerminalId("t" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _terminals.Add(id);

        // Как в настоящем наборе вкладок: открытая вкладка сразу становится видимой.
        VisibleTerminal = id;
        return Task.FromResult(id);
    }

    public Task ActivateAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        VisibleTerminal = terminalId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Токен, выданный вкладке при запуске. Настоящий набор вкладок выдаёт случайный;
    /// здесь достаточно предсказуемого — карта «токен → вкладка» проверяется тестами
    /// самого набора вкладок, а не корневой ViewModel.
    /// </summary>
    public string TokenFor(TerminalId terminalId) => "tok-" + terminalId.Value;

    public bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId)
    {
        foreach (var id in _terminals)
        {
            if (TokenFor(id) == correlationToken)
            {
                terminalId = id;
                return true;
            }
        }

        // Неинициализированный TerminalId бросает при обращении к Value, поэтому
        // на промахе возвращается default вместе с false — сравнивать его нельзя.
        terminalId = default;
        return false;
    }

    public Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        Closed.Add(terminalId);
        _terminals.Remove(terminalId);
        if (VisibleTerminal == terminalId)
        {
            VisibleTerminal = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>Сообщает, что процесс вкладки завершился.</summary>
    public void RaiseExited(TerminalId terminalId, int exitCode) =>
        TerminalExited?.Invoke(this, new TerminalExitedEventArgs(terminalId, exitCode));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Приёмник хуков без HttpListener: событие поднимается вручную из теста.</summary>
internal sealed class FakeHookListener : IHookListener
{
    public event EventHandler<HookEventArgs>? HookReceived;

    public Uri Endpoint { get; } = new("http://127.0.0.1:52100/hook/");

    /// <summary>Приёмник был поднят.</summary>
    public bool Started { get; private set; }

    /// <summary>Приёмник был освобождён.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Поднять приёмник не удалось — проверка мягкой деградации раздела 5.3 ТЗ.</summary>
    public Exception? StartFailure { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (StartFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Started = true;
        return Task.CompletedTask;
    }

    /// <summary>Сообщает о пришедшем хуке.</summary>
    public void Raise(HookKind kind, string? token, string? sessionId = null, string? workingDirectory = null) =>
        HookReceived?.Invoke(
            this,
            new HookEventArgs(new HookEvent(kind, sessionId, workingDirectory, token, DateTimeOffset.UnixEpoch)));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>История сессий без файловой системы.</summary>
internal sealed class FakeSessionHistoryReader : ISessionHistoryReader
{
    private readonly Dictionary<string, SessionSummary> _byId = [];

    /// <summary>Запрошенные пары «каталог, сессия» по порядку.</summary>
    public List<(string Directory, string SessionId)> Requested { get; } = [];

    /// <summary>Кладёт сводку, которую вернёт <see cref="ReadOneAsync" />.</summary>
    public void Seed(string sessionId, string? title) =>
        _byId[sessionId] = new SessionSummary(
            sessionId, $@"C:\transcripts\{sessionId}.jsonl", DateTimeOffset.UnixEpoch, 0, title, null, null);

    public Task<IReadOnlyList<SessionSummary>> ReadAsync(string workingDirectory, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>(_byId.Values.ToArray());

    /// <summary>Исключение, которым отвечает следующее чтение одной сессии.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>
    /// Задержки чтения по сессиям: пока задача не завершена, чтение этой сессии висит.
    /// Нужны, чтобы проверить, что делает координатор, когда транскрипт доезжает с опозданием.
    /// </summary>
    public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

    public async Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken)
    {
        // Чтения уходят в пул, поэтому список запросов пополняется под замком.
        lock (Requested)
        {
            Requested.Add((workingDirectory, sessionId));
        }

        if (ReadFailure is { } failure)
        {
            ReadFailure = null;
            throw failure;
        }

        if (Gates.TryGetValue(sessionId, out var gate))
        {
            await gate.Task;
        }

        return _byId.TryGetValue(sessionId, out var summary) ? summary : null;
    }
}

/// <summary>Полоса вкладок в памяти: запоминает всё, что ей выставил координатор состояний.</summary>
internal sealed class FakeTabStateSink : ITabStateSink
{
    private readonly Dictionary<TerminalId, string> _directories = [];

    /// <summary>Последнее выставленное состояние каждой вкладки.</summary>
    public Dictionary<TerminalId, TabState> States { get; } = [];

    /// <summary>Последнее выставленное короткое имя каждой вкладки.</summary>
    public Dictionary<TerminalId, string> ShortTitles { get; } = [];

    /// <summary>Все выставленные состояния по порядку — для проверки переходов.</summary>
    public List<(TerminalId Terminal, TabState State)> StateLog { get; } = [];

    /// <summary>Рабочий каталог вкладки; не заданный означает закрытую вкладку.</summary>
    public void SetWorkingDirectory(TerminalId terminalId, string workingDirectory) =>
        _directories[terminalId] = workingDirectory;

    public void SetState(TerminalId terminalId, TabState state)
    {
        States[terminalId] = state;
        StateLog.Add((terminalId, state));
    }

    public void SetShortTitle(TerminalId terminalId, string shortTitle) => ShortTitles[terminalId] = shortTitle;

    public void ResetShortTitle(TerminalId terminalId)
    {
        ShortTitles.Remove(terminalId);
        ResetTitles.Add(terminalId);
    }

    /// <summary>Вкладки, которым имя сбрасывали, по порядку — сброс обязан быть не безусловным.</summary>
    public List<TerminalId> ResetTitles { get; } = [];

    public bool TryGetWorkingDirectory(TerminalId terminalId, out string workingDirectory)
    {
        if (_directories.TryGetValue(terminalId, out var directory))
        {
            workingDirectory = directory;
            return true;
        }

        workingDirectory = string.Empty;
        return false;
    }
}

/// <summary>
/// Диспетчер, который копит работу вместо немедленного выполнения: так видно, что событие
/// действительно ушло в поток интерфейса, а не было обработано в потоке приёмника хуков.
/// </summary>
internal sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _pending = new();
    private int _postCount;

    /// <summary>Сколько работы было отправлено в поток интерфейса.</summary>
    public int PostCount
    {
        get
        {
            lock (_pending)
            {
                return _postCount;
            }
        }
    }

    /// <summary>Есть ли неисполненная работа.</summary>
    public bool HasPending
    {
        get
        {
            lock (_pending)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <remarks>
    /// Отправлять могут и потоки пула — настоящий диспетчер WPF тем и занят, — поэтому очередь
    /// под замком. Сами колбэки исполняет только <see cref="Drain"/>, то есть ровно один поток.
    /// </remarks>
    public void Post(Action action)
    {
        lock (_pending)
        {
            _postCount++;
            _pending.Enqueue(action);
        }
    }

    /// <summary>Выполняет накопленную работу — аналог прокрутки очереди диспетчера WPF.</summary>
    public void Drain()
    {
        while (true)
        {
            Action action;
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                action = _pending.Dequeue();
            }

            action();
        }
    }
}
