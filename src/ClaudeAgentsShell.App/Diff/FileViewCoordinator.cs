using System.Globalization;
using System.IO;
using System.Text;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Инструмент <c>show_file</c>: читает названные агентом файлы и показывает их в панели
/// вкладки в режиме «файл». Фоновая вкладка получает значок на кнопке diff, активная
/// не переключается.
/// </summary>
/// <remarks>
/// <para>
/// Панель вкладки общая с diff, её хозяин — <see cref="IDiffPanelHost"/>: файлы уходят на
/// страницу под его замком отправки, заодно списывая diff вкладки, и значок ставит он же.
/// Кто позже попросил панель, тот её и получает: новый <c>show_file</c> отменяет прежний,
/// а diff, открытый во время чтения файлов, отменяет их показ.
/// </para>
/// <para>
/// Вкладки трогаются только в потоке интерфейса (<see cref="IUiDispatcher"/>); файлы читаются
/// и отправляются на страницу в пуле — как у diff, <see cref="IFileView"/> зовётся из любого потока.
/// </para>
/// <para>
/// В конструкторе нет ни <see cref="IHookListener"/>, ни <see cref="ITerminalWorkspace"/> — по той же
/// причине, что у <see cref="DiffCoordinator"/>: маршрут <c>/mcp</c> зависит от <see cref="IShowFileHandler"/>.
/// </para>
/// </remarks>
public sealed class FileViewCoordinator : IShowFileHandler, IAsyncDisposable
{
    /// <summary>Сколько файлов одного вызова читается одновременно.</summary>
    internal const int MaxParallelReads = 4;

    /// <summary>
    /// Потолок суммарного текста одного вызова, в символах (~16 МБ для ASCII). Файлы сверх него
    /// по порядку агента показываются как слишком большие — страница и мост не тянут больше.
    /// </summary>
    internal const long MaxTotalChars = 16L * 1024 * 1024;

    private readonly IWorkspaceFileReader _reader;
    private readonly IFileView _view;
    private readonly IDiffView _panel;
    private readonly IDiffPanelHost _host;
    private readonly IUiDispatcher _dispatcher;

    private readonly object _gate = new();
    private readonly Dictionary<TerminalId, FileViewShow> _shows = [];
    private readonly HashSet<Task> _running = [];

    private IDiffTabs? _tabs;
    private bool _disposed;

    /// <inheritdoc cref="FileViewCoordinator" />
    /// <param name="reader">Чтение файлов рабочего каталога.</param>
    /// <param name="view">Панель вкладки в режиме «файл».</param>
    /// <param name="panel">Та же панель: нужно только её закрытие пользователем, общее для обоих режимов.</param>
    /// <param name="host">Хозяин панели — координатор diff.</param>
    /// <param name="dispatcher">Поток интерфейса: вызов <c>show_file</c> приходит из пула <c>HttpListener</c>.</param>
    public FileViewCoordinator(
        IWorkspaceFileReader reader,
        IFileView view,
        IDiffView panel,
        IDiffPanelHost host,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _reader = reader;
        _view = view;
        _panel = panel;
        _host = host;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Подключает набор вкладок и начинает слушать закрытие панели. Вызывается один раз, в потоке
    /// интерфейса. До вызова <c>show_file</c> отвечает «вкладка не найдена».
    /// </summary>
    public void Start(IDiffTabs tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tabs is not null)
            {
                throw new InvalidOperationException("Координатор просмотра файлов уже запущен.");
            }

            _tabs = tabs;
        }

        tabs.TabClosed += OnTabClosed;
        _panel.Closed += OnPanelClosed;
    }

    /// <summary>Незавершённые вызовы. Для тестов.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_running.ToArray());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Отвечает после отправки на страницу. Отмена <paramref name="cancellationToken"/> бросает
    /// ожидание, но не сам показ — как у <c>show_diff</c>.
    /// </remarks>
    public async Task<ShowFileOutcome> HandleAsync(
        string? correlationToken,
        ShowFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await OnUiAsync(() => ResolveTarget(correlationToken, request.Directory))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target is not { } resolved)
        {
            return new ShowFileOutcome.UnknownSession();
        }

        return await Begin(resolved.TerminalId, resolved.Directory, request)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Отменяет незавершённые вызовы и ждёт их; сами порты не освобождает.</remarks>
    public async ValueTask DisposeAsync()
    {
        IDiffTabs? tabs;
        FileViewShow[] shows;
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
            shows = [.. _shows.Values];
            _shows.Clear();
            running = [.. _running];
        }

        if (tabs is not null)
        {
            tabs.TabClosed -= OnTabClosed;
            _panel.Closed -= OnPanelClosed;
        }

        foreach (var show in shows)
        {
            show.Cancel();
        }

        // Работа сама ловит свои сбои, поэтому WhenAll не бросает.
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Вкладка и каталог вызова. Выполняется в потоке интерфейса.</summary>
    private (TerminalId TerminalId, string Directory)? ResolveTarget(string? correlationToken, string? requestedDirectory)
    {
        if (_tabs is not { } tabs
            || !tabs.TryResolveTerminal(correlationToken, out var terminalId)
            || tabs.Find(terminalId) is not { } tab)
        {
            return null;
        }

        return (terminalId, TabDirectories.Resolve(requestedDirectory, TabDirectories.Of(tab)));
    }

    /// <summary>Заводит вызов во вкладке, отменив прежний незавершённый, и запускает его.</summary>
    private Task<ShowFileOutcome> Begin(TerminalId terminalId, string directory, ShowFileRequest request)
    {
        var show = new FileViewShow(_host.DiffRequestStamp);
        FileViewShow? previous;
        Task<ShowFileOutcome> work;

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult<ShowFileOutcome>(Superseded());
            }

            _shows.TryGetValue(terminalId, out previous);
            _shows[terminalId] = show;

            // Учитывается под замком до запуска: DisposeAsync увидит и дождётся её.
            work = Task.Run(() => ShowAsync(terminalId, directory, request, show));
            show.Work = work;
            _running.Add(work);
        }

        previous?.Cancel();
        return work;
    }

    /// <summary>Читает и показывает файлы. Никогда не бросает.</summary>
    private async Task<ShowFileOutcome> ShowAsync(
        TerminalId terminalId,
        string directory,
        ShowFileRequest request,
        FileViewShow show)
    {
        var cancellationToken = show.Token;
        try
        {
            var root = await _reader.ResolveRootAsync(directory, cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                return new ShowFileOutcome.Failed("Directory not found: " + directory);
            }

            var files = await ReadAllAsync(root, directory, request.Files, cancellationToken).ConfigureAwait(false);
            if (files.All(file => file.File.Problem != ViewedFileProblem.None))
            {
                return new ShowFileOutcome.Failed("None of the files could be shown: " + DescribeProblems(files) + ".");
            }

            var set = new FileViewSet(root, TabDirectories.NullIfBlank(request.Note), [.. files.Select(file => file.File)]);
            var shown = await _host.ShowFilesAsync(
                    terminalId,
                    show.DiffStamp,
                    token => _view.ShowFilesAsync(terminalId, set, token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!shown)
            {
                return Superseded();
            }

            _dispatcher.Post(() => MarkIfBackground(terminalId));
            return new ShowFileOutcome.Shown(Summarize(files));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Superseded();
        }
        catch (Exception exception)
        {
            // Страница не приняла сообщение: окно закрывается или мост упал.
            return new ShowFileOutcome.Failed("Could not show the files: " + exception.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_shows.TryGetValue(terminalId, out var current) && ReferenceEquals(current, show))
                {
                    _shows.Remove(terminalId);
                }

                if (show.Work is { } work)
                {
                    _running.Remove(work);
                }
            }

            await show.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Читает файлы параллельно, не больше <see cref="MaxParallelReads"/> разом, в порядке агента.
    /// Когда уже прочитанного больше <see cref="MaxTotalChars"/>, остальные не читаются вовсе —
    /// память вызова ограничена потолком и несколькими файлами в работе; затем потолок
    /// применяется по порядку точно.
    /// </summary>
    private async Task<ReadFile[]> ReadAllAsync(
        string root,
        string directory,
        IReadOnlyList<ShowFileItem> items,
        CancellationToken cancellationToken)
    {
        var results = new ViewedFile[items.Count];
        var skipped = new bool[items.Count];
        long readChars = 0;

        using (var limiter = new SemaphoreSlim(MaxParallelReads, MaxParallelReads))
        {
            async Task ReadOneAsync(int index)
            {
                await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var item = items[index];
                    if (Interlocked.Read(ref readChars) >= MaxTotalChars)
                    {
                        results[index] = Unshown(root, directory, item, ViewedFileProblem.TooLarge);
                        skipped[index] = true;
                        return;
                    }

                    var file = await ReadSafeAsync(root, directory, item, cancellationToken).ConfigureAwait(false);
                    results[index] = file;
                    Interlocked.Add(ref readChars, file.Text?.Length ?? 0);
                }
                finally
                {
                    limiter.Release();
                }
            }

            await Task.WhenAll(Enumerable.Range(0, items.Count).Select(ReadOneAsync)).ConfigureAwait(false);
        }

        var budget = MaxTotalChars;
        var files = new ReadFile[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var file = results[i];
            var overTotal = skipped[i];

            if (file.Text is { } text)
            {
                if (text.Length > budget)
                {
                    file = file with { Text = null, Problem = ViewedFileProblem.TooLarge };
                    overTotal = true;
                }
                else
                {
                    budget -= text.Length;
                }
            }

            files[i] = new ReadFile(file, overTotal);
        }

        return files;
    }

    /// <summary>Читатель не бросает по контракту; всё же брошенное — «не прочитан», а не сбой вызова.</summary>
    private async Task<ViewedFile> ReadSafeAsync(string root, string directory, ShowFileItem item, CancellationToken cancellationToken)
    {
        try
        {
            return await _reader.ReadAsync(root, directory, item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unshown(root, directory, item, ViewedFileProblem.Unreadable);
        }
    }

    /// <summary>
    /// Файл, до чтения которого не дошло. Путь — относительно корня, как у прочитанных; если
    /// так не выходит (путь не разбирается или ведёт вне корня) — как у агента, через <c>/</c>.
    /// </summary>
    private static ViewedFile Unshown(string root, string directory, ShowFileItem item, ViewedFileProblem problem) =>
        new(DisplayPath(root, directory, item.Path), Text: null, item.Focus, problem);

    private static string DisplayPath(string root, string directory, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(directory, path)));
            if (relative != ".."
                && !relative.StartsWith(@"..\", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                path = relative;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Путь не разбирается — показываем как есть.
        }

        return path.Replace('\\', '/');
    }

    /// <summary>Значок фоновой вкладке, у которой только что показаны файлы. Поток интерфейса.</summary>
    private void MarkIfBackground(TerminalId terminalId)
    {
        if (_tabs?.Find(terminalId) is { IsActive: false } tab)
        {
            _host.SetPendingBadge(tab);
        }
    }

    /// <summary>Пользователь закрыл панель — файлы, которые ещё читаются, её не откроют. Поток интерфейса.</summary>
    private void OnPanelClosed(object? sender, DiffClosedEventArgs e) => Cancel(e.TerminalId);

    /// <summary>Вкладка закрыта. Поток интерфейса.</summary>
    private void OnTabClosed(object? sender, DiffTabClosedEventArgs e) => Cancel(e.TerminalId);

    private void Cancel(TerminalId terminalId)
    {
        FileViewShow? show;
        lock (_gate)
        {
            if (!_shows.Remove(terminalId, out show))
            {
                return;
            }
        }

        show.Cancel();
    }

    /// <summary>Выполняет работу в потоке интерфейса и отдаёт её результат; продолжения — в пуле.</summary>
    private Task<T> OnUiAsync<T>(Func<T> work)
    {
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

    /// <summary>Итог для агента: что показано и что нет.</summary>
    private string Summarize(IReadOnlyList<ReadFile> files)
    {
        var shown = files.Where(file => file.File.Problem == ViewedFileProblem.None).ToArray();
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Shown to the user: {shown.Length} {(shown.Length == 1 ? "file" : "files")} (");
        builder.AppendJoin(", ", shown.Select(file => file.File.Focus is { } focus
            ? string.Create(CultureInfo.InvariantCulture, $"{file.File.Path} lines {focus.From}-{focus.To}")
            : file.File.Path));
        builder.Append(')');

        if (shown.Length < files.Count)
        {
            builder.Append(". Not shown: ").Append(DescribeProblems(files));
        }

        return builder.Append('.').ToString();
    }

    private string DescribeProblems(IReadOnlyList<ReadFile> files) =>
        string.Join("; ", files
            .Where(file => file.File.Problem != ViewedFileProblem.None)
            .Select(file => file.File.Path + " - " + Describe(file)));

    private string Describe(ReadFile file) => file.File.Problem switch
    {
        ViewedFileProblem.TooLarge when file.OverTotal =>
            string.Create(CultureInfo.InvariantCulture, $"over the {MaxTotalChars / (1024 * 1024)} MB total for one call, show it separately"),
        ViewedFileProblem.TooLarge => "larger than the " + FormatMegabytes(_reader.MaxFileBytes) + " limit",
        ViewedFileProblem.NotFound => "not found or is a directory",
        ViewedFileProblem.OutsideRoot => "outside the workspace root",
        ViewedFileProblem.Binary => "binary file",
        ViewedFileProblem.Unreadable => "could not be read (locked or access denied)",
        _ => file.File.Problem.ToString(),
    };

    private static string FormatMegabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.##} MB");

    /// <summary>
    /// Вызов сменил новый, панель или вкладку закрыли, пользователь открыл diff. Для агента это
    /// не ошибка: иначе он повторил бы вызов и снова открыл панель, которую пользователь закрыл.
    /// </summary>
    private static ShowFileOutcome.Shown Superseded() =>
        new("The panel was closed or replaced by a newer request before the files were shown.");

    /// <summary>Файл вызова и то, что он не показан из-за общего потолка вызова.</summary>
    private readonly record struct ReadFile(ViewedFile File, bool OverTotal);
}
