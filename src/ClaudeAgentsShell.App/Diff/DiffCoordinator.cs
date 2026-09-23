using System.ComponentModel;
using System.Globalization;
using System.IO;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Панель diff вкладок (issue #5): открывает её по клавише и по вызову <c>show_diff</c>,
/// отвечает на просьбы страницы (файл, обновить, закрыть), ставит значок фоновой вкладке
/// и плашку «есть изменения».
/// </summary>
/// <remarks>
/// <para>
/// На вкладку — одно живое <see cref="DiffGeneration"/>. Новый запрос оглавления списывает
/// прежнее вместе с его файловыми запросами, а каждое сообщение на страницу уходит под общим
/// замком отправки с проверкой, что поколение всё ещё текущее, — поэтому устаревший ответ
/// git не долетает до панели после нового запроса.
/// </para>
/// <para>
/// Вкладки трогаются только в потоке интерфейса (<see cref="IUiDispatcher"/>); git и отправка
/// на страницу идут из любого потока. От <see cref="IDiffView"/> требуется, чтобы его можно
/// было звать из любого потока, а сообщения доходили на страницу в порядке вызовов.
/// </para>
/// <para>
/// В конструкторе нет ни <see cref="IHookListener"/>, ни <see cref="ITerminalWorkspace"/>:
/// маршрут <c>/mcp</c> живёт в приёмнике хуков и зависит от <see cref="IShowDiffHandler"/>,
/// так что обе зависимости замкнули бы граф контейнера в кольцо. Токены разрешает
/// <see cref="IDiffTabs"/>, хуки приносит <see cref="DiffStaleTracker"/>.
/// </para>
/// </remarks>
public sealed class DiffCoordinator : IShowDiffHandler, IDiffChangeSink, IAsyncDisposable
{
    /// <summary>
    /// Сколько <c>git</c> на файлы одна вкладка может держать одновременно. Страница раскрывает
    /// до 50 файлов разом, и без потолка прокрутка оглавления подняла бы сотню процессов.
    /// </summary>
    internal const int MaxParallelFileReads = 3;

    private const string FileNotInIndexMessage = "Файла нет в оглавлении — обновите diff.";

    private readonly IGitDiffReader _git;
    private readonly IDiffView _view;
    private readonly IUiDispatcher _dispatcher;

    private readonly object _gate = new();
    private readonly Dictionary<TerminalId, DiffGeneration> _panels = [];
    private readonly Dictionary<TerminalId, TabViewModel> _badged = [];
    private readonly HashSet<Task> _running = [];

    /// <summary>Сообщения на страницу уходят по одному: проверка поколения и отправка неразрывны.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private IDiffTabs? _tabs;
    private bool _disposed;

    /// <inheritdoc cref="DiffCoordinator" />
    /// <param name="git">Чтение diff из git.</param>
    /// <param name="view">Панель diff на странице терминалов.</param>
    /// <param name="dispatcher">Поток интерфейса: вызов <c>show_diff</c> приходит из пула <c>HttpListener</c>.</param>
    public DiffCoordinator(IGitDiffReader git, IDiffView view, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _git = git;
        _view = view;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Подключает набор вкладок и начинает слушать панель. Вызывается один раз, в потоке
    /// интерфейса. До вызова <c>show_diff</c> отвечает «вкладка не найдена».
    /// </summary>
    public void Start(IDiffTabs tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tabs is not null)
            {
                throw new InvalidOperationException("Координатор diff уже запущен.");
            }

            _tabs = tabs;
        }

        tabs.TabClosed += OnTabClosed;
        _view.RefreshRequested += OnRefreshRequested;
        _view.FileRequested += OnFileRequested;
        _view.Closed += OnPanelClosed;
    }

    /// <summary>
    /// Незавершённая работа: построения и файловые запросы. Для тестов — дождаться, пока
    /// запрос, начатый событием страницы, дойдёт до панели.
    /// </summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_running.ToArray());
        }
    }

    /// <summary>
    /// Открывает панель вкладки по <c>Ctrl+Shift+D</c> или кнопке: каталог — текущий каталог
    /// главного агента, до первого хука — каталог запуска; база выбирается сама.
    /// Завершается, когда оглавление показано или показан сбой.
    /// </summary>
    public async Task OpenForTabAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        var directory = await OnUiAsync(() => _tabs?.Find(terminalId) is { } tab ? DirectoryOf(tab) : null)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (directory is null)
        {
            return;
        }

        var query = new DiffQuery(directory, BaseRef: null, IgnoreWhitespace: false, Files: [], Note: null);
        await Track(BuildAsync(terminalId, query, fromAgentCall: false)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Отвечает только после того, как оглавление построено: агенту нужен итог — сколько файлов
    /// и против какой базы, либо причина сбоя. Фоновая вкладка получает значок, активная
    /// вкладка не переключается. Отмена <paramref name="cancellationToken"/> бросает ожидание,
    /// но не само построение: панель уже открыта у пользователя.
    /// </remarks>
    public async Task<ShowDiffOutcome> HandleAsync(
        string? correlationToken,
        ShowDiffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await OnUiAsync(() => ResolveShowDiffTarget(correlationToken, request.Directory))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target is not { } resolved)
        {
            return new ShowDiffOutcome.UnknownSession();
        }

        var query = new DiffQuery(
            resolved.Directory,
            NullIfBlank(request.BaseRef),
            IgnoreWhitespace: false,
            Files: CleanFiles(request.Files),
            Note: NullIfBlank(request.Note));

        return await Track(BuildAsync(resolved.TerminalId, query, fromAgentCall: true))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Хук не называет инструменты пачки, поэтому у панели, открытой агентом, пропускается
    /// первая пачка после ответа — та, в которой шёл сам <c>show_diff</c>. Правка, сделанная
    /// в той же параллельной пачке, что и вызов, плашку не даст; сабагент, закончивший пачку
    /// раньше главного агента, «съест» пропуск. Оба случая — принятое ограничение.
    /// </remarks>
    public void NotifyFilesChanged(string? correlationToken)
    {
        if (_tabs is not { } tabs || !tabs.TryResolveTerminal(correlationToken, out var terminalId))
        {
            return;
        }

        DiffGeneration? generation;
        lock (_gate)
        {
            if (_disposed || !_panels.TryGetValue(terminalId, out generation) || generation.StaleMarked)
            {
                return;
            }

            if (generation.Index is null)
            {
                // Оглавление ещё строится: снимок, возможно, уже устарел — плашку покажет
                // само построение, когда отправит оглавление. Пачка с самим вызовом show_diff
                // сюда попасть не может: она ждёт ответа, а ответ — после оглавления.
                generation.ChangedWhileBuilding = true;
                return;
            }

            if (generation.SkipCallerBatch)
            {
                generation.SkipCallerBatch = false;
                return;
            }

            generation.StaleMarked = true;
        }

        Track(MarkStaleAsync(terminalId, generation));
    }

    /// <inheritdoc />
    /// <remarks>Отменяет всю работу и ждёт её завершения; сам <see cref="IDiffView"/> не освобождает.</remarks>
    public async ValueTask DisposeAsync()
    {
        IDiffTabs? tabs;
        DiffGeneration[] generations;
        KeyValuePair<TerminalId, TabViewModel>[] badged;
        Task[] running;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tabs = _tabs;
            _tabs = null;
            generations = [.. _panels.Values];
            _panels.Clear();
            badged = [.. _badged];
            _badged.Clear();
            running = [.. _running];
        }

        if (tabs is not null)
        {
            tabs.TabClosed -= OnTabClosed;
            _view.RefreshRequested -= OnRefreshRequested;
            _view.FileRequested -= OnFileRequested;
            _view.Closed -= OnPanelClosed;
        }

        foreach (var (_, tab) in badged)
        {
            tab.PropertyChanged -= OnBadgedTabPropertyChanged;
        }

        foreach (var generation in generations)
        {
            generation.Retire();
        }

        // Задачи сами ловят свои сбои, поэтому WhenAll не бросает.
        await Task.WhenAll(running).ConfigureAwait(false);
        _sendGate.Dispose();
    }

    /// <summary>Разрешает вызов <c>show_diff</c> во вкладку. Выполняется в потоке интерфейса.</summary>
    private (TerminalId TerminalId, string Directory)? ResolveShowDiffTarget(string? correlationToken, string? requestedDirectory)
    {
        if (_tabs is not { } tabs
            || !tabs.TryResolveTerminal(correlationToken, out var terminalId)
            || tabs.Find(terminalId) is not { } tab)
        {
            return null;
        }

        if (!tab.IsActive)
        {
            SetPendingBadge(tab);
        }

        return (terminalId, ResolveDirectory(requestedDirectory, DirectoryOf(tab)));
    }

    /// <summary>
    /// Значок diff на фоновой вкладке. Снимается, когда вкладка станет активной любым путём:
    /// клик, сочетание, закрытие соседки, смена проекта. Выполняется в потоке интерфейса.
    /// </summary>
    private void SetPendingBadge(TabViewModel tab)
    {
        tab.HasPendingDiff = true;

        lock (_gate)
        {
            if (_disposed || !_badged.TryAdd(tab.TerminalId, tab))
            {
                return;
            }
        }

        tab.PropertyChanged += OnBadgedTabPropertyChanged;
    }

    private void OnBadgedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabViewModel { IsActive: true } tab || e.PropertyName != nameof(TabViewModel.IsActive))
        {
            return;
        }

        tab.HasPendingDiff = false;
        ForgetBadge(tab.TerminalId);
    }

    private void ForgetBadge(TerminalId terminalId)
    {
        TabViewModel? tab;
        lock (_gate)
        {
            if (!_badged.Remove(terminalId, out tab))
            {
                return;
            }
        }

        tab.PropertyChanged -= OnBadgedTabPropertyChanged;
    }

    /// <summary>
    /// Строит панель: «строится» → оглавление и рабочие деревья параллельно → оглавление или сбой.
    /// Никогда не бросает: итог — для агента, сбой уже показан в панели.
    /// </summary>
    /// <param name="terminalId">Вкладка.</param>
    /// <param name="query">Что сравнивать.</param>
    /// <param name="fromAgentCall">
    /// Построение идёт по вызову <c>show_diff</c>: пачка, в которой шёл вызов, закончится уже
    /// после ответа, и её <c>PostToolBatch</c> плашку не ставит.
    /// </param>
    private async Task<ShowDiffOutcome> BuildAsync(TerminalId terminalId, DiffQuery query, bool fromAgentCall)
    {
        var generation = new DiffGeneration(query, MaxParallelFileReads) { SkipCallerBatch = fromAgentCall };
        DiffGeneration? previous;

        lock (_gate)
        {
            if (_disposed)
            {
                generation.Retire();
                return Superseded();
            }

            _panels.TryGetValue(terminalId, out previous);
            _panels[terminalId] = generation;
        }

        previous?.Retire();

        if (!generation.TryEnter(out var cancellationToken))
        {
            return Superseded();
        }

        try
        {
            if (!await SendAsync(terminalId, generation, token => _view.ShowPendingAsync(terminalId, token), cancellationToken)
                    .ConfigureAwait(false))
            {
                return Superseded();
            }

            var request = new DiffRequest(query.Directory, query.BaseRef, query.Files, query.IgnoreWhitespace);
            var worktreesTask = ListWorktreesAsync(query.Directory, cancellationToken);

            DiffIndex index;
            try
            {
                index = await _git.ListChangesAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await worktreesTask.ConfigureAwait(false);
                var message = exception is DiffUnavailableException
                    ? exception.Message
                    : "Не удалось построить diff: " + exception.Message;

                await SendAsync(
                        terminalId,
                        generation,
                        token => _view.ShowErrorAsync(terminalId, path: null, message, token),
                        cancellationToken)
                    .ConfigureAwait(false);

                return new ShowDiffOutcome.Failed(message);
            }

            var worktrees = await worktreesTask.ConfigureAwait(false);

            var shown = await SendAsync(
                    terminalId,
                    generation,
                    token =>
                    {
                        // Под замком отправки: файловый запрос, пришедший сразу после показа
                        // оглавления, уже найдёт его.
                        lock (_gate)
                        {
                            generation.Index = index;
                        }

                        // Разворот сверяется с путями оглавления точным совпадением — пути агента
                        // приводятся к той же форме, что и при сужении оглавления.
                        var expand = DiffPaths.NormalizeRequested(query.Files, index.RepositoryRoot).Paths;
                        return _view.ShowIndexAsync(terminalId, index, worktrees, query.Note, expand, token);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!shown)
            {
                return Superseded();
            }

            if (TakeChangeDuringBuild(generation))
            {
                await SendAsync(terminalId, generation, token => _view.MarkStaleAsync(terminalId, token), cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ShowDiffOutcome.Shown(string.Create(
                CultureInfo.InvariantCulture,
                $"Shown to the user: {index.Files.Count} files vs {index.BaseRef}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Superseded();
        }
        catch (Exception exception)
        {
            // Страница не приняла сообщение: окно закрывается или мост упал. Приложению
            // падать нельзя, агенту — честный отказ.
            return new ShowDiffOutcome.Failed("Не удалось показать diff: " + exception.Message);
        }
        finally
        {
            generation.Exit();
        }
    }

    /// <summary>Рабочие деревья для переключателя; любой сбой — пустой список.</summary>
    private async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await _git.ListWorktreesAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private bool TakeChangeDuringBuild(DiffGeneration generation)
    {
        lock (_gate)
        {
            if (!generation.ChangedWhileBuilding || generation.StaleMarked)
            {
                return false;
            }

            generation.StaleMarked = true;
            return true;
        }
    }

    private async Task MarkStaleAsync(TerminalId terminalId, DiffGeneration generation)
    {
        if (!generation.TryEnter(out var cancellationToken))
        {
            return;
        }

        try
        {
            await SendAsync(terminalId, generation, token => _view.MarkStaleAsync(terminalId, token), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Отменили или страница не приняла — плашка не нужна.
        }
        finally
        {
            generation.Exit();
        }
    }

    /// <summary>Страница раскрыла файл. Событие приходит из потока интерфейса.</summary>
    private void OnFileRequested(object? sender, DiffFileRequestedEventArgs e) => Track(LoadFileAsync(e));

    private async Task LoadFileAsync(DiffFileRequestedEventArgs request)
    {
        var terminalId = request.TerminalId;
        DiffGeneration? generation;
        DiffIndex? index;

        lock (_gate)
        {
            if (_disposed || !_panels.TryGetValue(terminalId, out generation))
            {
                return;
            }

            index = generation.Index;
        }

        if (!generation.TryEnter(out var generationToken))
        {
            return;
        }

        var load = new FileLoad(CancellationTokenSource.CreateLinkedTokenSource(generationToken));
        FileLoad? replaced;
        lock (_gate)
        {
            generation.FileLoads.TryGetValue(request.Path, out replaced);
            generation.FileLoads[request.Path] = load;
        }

        // Вне замка: колбэки отмены исполняются синхронно.
        replaced?.Cancel();

        // Вызывается только под замком координатора.
        bool StillWanted() =>
            generation.FileLoads.TryGetValue(request.Path, out var current) && ReferenceEquals(current, load);

        var cancellationToken = load.Token;
        try
        {
            var entry = index?.Files.FirstOrDefault(file => string.Equals(file.Path, request.Path, StringComparison.Ordinal));
            if (index is null || entry is null)
            {
                await SendAsync(
                        terminalId,
                        generation,
                        token => _view.ShowErrorAsync(terminalId, request.Path, FileNotInIndexMessage, token),
                        cancellationToken,
                        StillWanted)
                    .ConfigureAwait(false);
                return;
            }

            FileDiff diff;
            await generation.FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                diff = await _git.ReadFileDiffAsync(index, entry, request.Context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                generation.FileGate.Release();
            }

            await SendAsync(
                    terminalId,
                    generation,
                    token => _view.ShowFileAsync(terminalId, diff, token),
                    cancellationToken,
                    StillWanted)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReportFileFailureAsync(terminalId, generation, request.Path, exception, StillWanted).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (StillWanted())
                {
                    generation.FileLoads.Remove(request.Path);
                }
            }

            load.Dispose();
            generation.Exit();
        }
    }

    /// <summary>
    /// Ошибка у файла — если страница его всё ещё ждёт: сбой git, таймаут, отказ моста.
    /// Если загрузку сменила новая просьба того же пути, новое оглавление или закрытие панели,
    /// ошибка не шлётся. Сам никогда не бросает.
    /// </summary>
    private async Task ReportFileFailureAsync(
        TerminalId terminalId,
        DiffGeneration generation,
        string path,
        Exception exception,
        Func<bool> stillWanted)
    {
        var message = exception switch
        {
            DiffUnavailableException => exception.Message,
            OperationCanceledException => "Загрузка файла прервана — раскройте его ещё раз.",
            _ => "Не удалось загрузить diff файла: " + exception.Message,
        };

        if (!generation.TryEnter(out var generationToken))
        {
            return;
        }

        try
        {
            await SendAsync(
                    terminalId,
                    generation,
                    token => _view.ShowErrorAsync(terminalId, path, message, token),
                    generationToken,
                    stillWanted)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Страница не приняла и сообщение об ошибке — окно закрывается.
        }
        finally
        {
            generation.Exit();
        }
    }

    /// <summary>Страница просит перестроить diff. Событие приходит из потока интерфейса.</summary>
    private void OnRefreshRequested(object? sender, DiffRefreshRequestedEventArgs e) => Track(RefreshAsync(e));

    private async Task RefreshAsync(DiffRefreshRequestedEventArgs request)
    {
        var terminalId = request.TerminalId;
        DiffQuery? previous;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _panels.TryGetValue(terminalId, out var generation) ? generation.Query : null;
        }

        try
        {
            // Панели без запроса нет только если её успели закрыть — тогда перестраиваем
            // от каталога вкладки, как по клавише.
            previous ??= await OnUiAsync(() => _tabs?.Find(terminalId) is { } tab
                    ? new DiffQuery(DirectoryOf(tab), BaseRef: null, IgnoreWhitespace: false, Files: [], Note: null)
                    : null)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        if (previous is null)
        {
            return;
        }

        var query = previous with
        {
            Directory = NullIfBlank(request.Directory) ?? previous.Directory,
            BaseRef = NullIfBlank(request.BaseRef) ?? previous.BaseRef,
            IgnoreWhitespace = request.IgnoreWhitespace,
        };

        await BuildAsync(terminalId, query, fromAgentCall: false).ConfigureAwait(false);
    }

    /// <summary>Пользователь закрыл панель. Событие приходит из потока интерфейса.</summary>
    private void OnPanelClosed(object? sender, DiffClosedEventArgs e) => Forget(e.TerminalId);

    /// <summary>Вкладка закрыта. Событие приходит из потока интерфейса.</summary>
    private void OnTabClosed(object? sender, DiffTabClosedEventArgs e)
    {
        Forget(e.TerminalId);
        ForgetBadge(e.TerminalId);
    }

    /// <summary>Отменяет всю работу панели вкладки и забывает её состояние.</summary>
    private void Forget(TerminalId terminalId)
    {
        DiffGeneration? generation;
        lock (_gate)
        {
            if (!_panels.Remove(terminalId, out generation))
            {
                return;
            }
        }

        generation.Retire();
    }

    /// <summary>
    /// Отправляет сообщение на страницу, если поколение ещё текущее и (для файла) просьба ещё
    /// не сменилась новой — <paramref name="stillWanted"/> проверяется под замком координатора.
    /// <c>false</c> — сообщение не ушло.
    /// </summary>
    /// <remarks>
    /// Отправка ждётся до конца под замком отправки: следующее сообщение уходит в мост только
    /// после того, как предыдущее (у файла — все его части) действительно передано странице.
    /// Поэтому порядок не зависит от того, как мост раскладывает части по заходам диспетчера.
    /// </remarks>
    private async Task<bool> SendAsync(
        TerminalId terminalId,
        DiffGeneration generation,
        Func<CancellationToken, ValueTask> send,
        CancellationToken cancellationToken,
        Func<bool>? stillWanted = null)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed
                    || cancellationToken.IsCancellationRequested
                    || !_panels.TryGetValue(terminalId, out var current)
                    || !ReferenceEquals(current, generation)
                    || (stillWanted is not null && !stillWanted()))
                {
                    return false;
                }
            }

            await send(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Выполняет работу в потоке интерфейса и отдаёт её результат.</summary>
    private Task<T> OnUiAsync<T>(Func<T> work)
    {
        // Продолжения — не в потоке интерфейса: за ожиданием идут git и отправка на страницу.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        return completion.Task;
    }

    /// <summary>Учитывает задачу, чтобы <see cref="DisposeAsync"/> её дождался.</summary>
    private Task<T> Track<T>(Task<T> task)
    {
        Track((Task)task);
        return task;
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            if (task.IsCompleted)
            {
                return;
            }

            _running.Add(task);
        }

        task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _running.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Каталог вкладки для diff: текущий каталог главного агента, до первого хука — каталог запуска.</summary>
    private static string DirectoryOf(TabViewModel tab) =>
        NullIfBlank(tab.CurrentDirectory) ?? tab.WorkingDirectory;

    /// <summary>
    /// Каталог из <c>show_diff</c>. Относительный считается от каталога вкладки: иначе git
    /// разрешил бы его от рабочего каталога приложения.
    /// </summary>
    private static string ResolveDirectory(string? requested, string tabDirectory)
    {
        if (NullIfBlank(requested) is not { } directory)
        {
            return tabDirectory;
        }

        if (Path.IsPathRooted(directory))
        {
            return directory;
        }

        var combined = Path.Combine(tabDirectory, directory);
        try
        {
            return Path.GetFullPath(combined);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return combined;
        }
    }

    /// <summary>Список файлов агента: из JSON может прийти <c>null</c> и пустые строки.</summary>
    private static IReadOnlyList<string> CleanFiles(IReadOnlyList<string>? files) =>
        files is null ? [] : [.. files.Where(file => !string.IsNullOrWhiteSpace(file))];

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Запрос заменён новым или панель закрыта, пока строилось оглавление. Для агента это не
    /// ошибка: иначе он повторил бы вызов и снова открыл панель, которую пользователь закрыл.
    /// </summary>
    private static ShowDiffOutcome.Shown Superseded() =>
        new("The diff panel was closed or replaced by a newer diff request before this one was built.");
}
