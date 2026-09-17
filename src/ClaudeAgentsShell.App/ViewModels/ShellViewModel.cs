using System.Windows.Input;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Корневая ViewModel окна: связывает панель проектов и полосу вкладок с набором терминалов.
/// Весь разговор с миром идёт через порты — ни файлов, ни процессов, ни WebView2 здесь нет.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITerminalWorkspace _workspace;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;

    private bool _disposed;

    /// <inheritdoc cref="ShellViewModel" />
    public ShellViewModel(
        ITerminalWorkspace workspace,
        ProjectListViewModel projects,
        IUserPrompt prompt,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _workspace = workspace;
        _prompt = prompt;
        _dispatcher = dispatcher;

        Projects = projects;
        Tabs = new TabStripViewModel();

        AddProjectCommand = new AsyncRelayCommand(_ => AddProjectAsync(CancellationToken.None), onError: ReportError);
        OpenSessionCommand = new AsyncRelayCommand(
            parameter => parameter is ProjectRowViewModel row
                ? OpenSessionAsync(row, CancellationToken.None)
                : Task.CompletedTask,
            parameter => parameter is ProjectRowViewModel { IsAvailable: true },
            ReportError);
        SelectProjectCommand = new AsyncRelayCommand(
            parameter => parameter is ProjectRowViewModel row
                ? ActivateProjectAsync(row, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);

        // Окно истории сессий — этап M3. Кнопка на строке проекта есть, но пока отключена.
        ShowHistoryCommand = new RelayCommand(static _ => { }, static _ => false);

        NewSessionCommand = new AsyncRelayCommand(
            _ => OpenSessionInActiveProjectAsync(CancellationToken.None),
            onError: ReportError);
        ActivateTabCommand = new AsyncRelayCommand(
            parameter => parameter is TabViewModel tab
                ? ActivateTabAsync(tab, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);
        CloseTabCommand = new AsyncRelayCommand(
            parameter => parameter is TabViewModel tab
                ? CloseTabAsync(tab, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);

        _workspace.TerminalExited += OnTerminalExited;
    }

    /// <summary>Панель проектов.</summary>
    public ProjectListViewModel Projects { get; }

    /// <summary>Полоса вкладок.</summary>
    public TabStripViewModel Tabs { get; }

    /// <summary>Добавить проект в список.</summary>
    public ICommand AddProjectCommand { get; }

    /// <summary>Новая сессия в проекте строки (кнопка «плюс» на строке).</summary>
    public ICommand OpenSessionCommand { get; }

    /// <summary>Клик по строке проекта.</summary>
    public ICommand SelectProjectCommand { get; }

    /// <summary>История сессий проекта. Отключена до этапа M3.</summary>
    public ICommand ShowHistoryCommand { get; }

    /// <summary>Новая сессия в активном проекте (кнопка «плюс» в полосе вкладок).</summary>
    public ICommand NewSessionCommand { get; }

    /// <summary>Переключиться на вкладку.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Закрыть вкладку.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>Строка проекта, которому принадлежит активная вкладка; <c>null</c>, если вкладок нет.</summary>
    public ProjectRowViewModel? ActiveProjectRow =>
        Tabs.ActiveTab is { } tab ? Projects.Rows.FirstOrDefault(row => row.Id == tab.ProjectId) : null;

    /// <summary>Поднимает страницу терминалов и читает список проектов.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _workspace.StartAsync(cancellationToken).ConfigureAwait(true);
        await Projects.LoadAsync(cancellationToken).ConfigureAwait(true);
        RefreshSessionCounts();
    }

    /// <summary>Добавляет проект через диалог выбора папки.</summary>
    public async Task AddProjectAsync(CancellationToken cancellationToken)
    {
        var row = await Projects.AddProjectAsync(cancellationToken).ConfigureAwait(true);
        if (row is not null)
        {
            RefreshSessionCounts();
        }
    }

    /// <summary>
    /// Открывает новую сессию в проекте и делает её вкладку активной.
    /// Каталог проекта исчез — запуск заблокирован, строка помечена недоступной (раздел 8 ТЗ).
    /// </summary>
    /// <returns>Открытая вкладка либо <c>null</c>, если запуск не состоялся.</returns>
    public async Task<TabViewModel?> OpenSessionAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!Projects.RefreshAvailability(row))
        {
            _prompt.ShowError(
                "Каталог недоступен",
                $"Каталог проекта «{row.Name}» недоступен: {row.Path}");
            return null;
        }

        TerminalId terminalId;
        try
        {
            terminalId = await _workspace
                .OpenAsync(row.Project, new SessionLaunch.NewSession(), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is PtyStartException or ShellNotFoundException)
        {
            // Обе ветки предусмотрены контрактом: сессия не открылась, но окно живёт дальше.
            _prompt.ShowError("Не удалось открыть сессию", exception.Message);
            return null;
        }

        var tab = new TabViewModel(terminalId, row.Id, row.Name);
        Tabs.Add(tab);

        // Открытую вкладку страница показывает сама внутри OpenAsync — второй показ
        // был бы лишним разговором с мостом. Здесь остаётся только состояние ViewModel.
        Tabs.SetActive(tab);
        RefreshSessionCounts();
        return tab;
    }

    /// <summary>Новая сессия в активном проекте: и для кнопки полосы вкладок, и для <c>Ctrl+Shift+T</c>.</summary>
    public async Task<TabViewModel?> OpenSessionInActiveProjectAsync(CancellationToken cancellationToken)
    {
        var row = ActiveProjectRow;
        return row is null ? null : await OpenSessionAsync(row, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Клик по строке проекта: переключает на активную вкладку проекта.
    /// Открытых вкладок нет — не делает ничего.
    /// </summary>
    public async Task ActivateProjectAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Tabs.ActiveTabFor(row.Id) is { } tab)
        {
            await ActivateTabAsync(tab, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Делает вкладку видимой: на странице меняется видимость контейнера, не более.</summary>
    public async Task ActivateTabAsync(TabViewModel tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (ReferenceEquals(Tabs.ActiveTab, tab))
        {
            return;
        }

        await _workspace.ActivateAsync(tab.TerminalId, cancellationToken).ConfigureAwait(true);
        Tabs.SetActive(tab);
        RefreshCurrentProject();
    }

    /// <summary>
    /// Закрывает вкладку. Живой процесс — сначала подтверждение: отказ оставляет вкладку на месте.
    /// </summary>
    /// <returns><c>true</c>, если вкладка закрыта.</returns>
    public async Task<bool> CloseTabAsync(TabViewModel tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!Tabs.Tabs.Contains(tab))
        {
            // Вкладку уже закрыли: повторный вызов приходит от удержанного Ctrl+Shift+W,
            // пока на экране висело подтверждение предыдущего.
            return false;
        }

        if (tab.IsRunning && !_prompt.Confirm(
                "Закрыть вкладку",
                $"В сессии «{tab.Title}» ещё работает процесс. Закрыть вкладку?"))
        {
            return false;
        }

        await _workspace.CloseAsync(tab.TerminalId, cancellationToken).ConfigureAwait(true);

        var next = Tabs.Remove(tab);
        RefreshSessionCounts();

        if (next is null)
        {
            Tabs.SetActive(null);
            RefreshCurrentProject();
            return true;
        }

        await ActivateTabAsync(next, cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>Выполняет оконную команду, назначенную сочетанию клавиш.</summary>
    /// <param name="shortcut">Распознанная команда.</param>
    /// <param name="tabNumber">Номер вкладки для <see cref="ShellShortcut.SelectTab"/>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task ApplyShortcutAsync(ShellShortcut shortcut, int tabNumber, CancellationToken cancellationToken)
    {
        switch (shortcut)
        {
            case ShellShortcut.NewSession:
                await OpenSessionInActiveProjectAsync(cancellationToken).ConfigureAwait(true);
                break;

            case ShellShortcut.CloseTab:
                if (Tabs.ActiveTab is { } active)
                {
                    await CloseTabAsync(active, cancellationToken).ConfigureAwait(true);
                }

                break;

            case ShellShortcut.NextTab:
                await ActivateIfAnyAsync(Tabs.Next(), cancellationToken).ConfigureAwait(true);
                break;

            case ShellShortcut.PreviousTab:
                await ActivateIfAnyAsync(Tabs.Previous(), cancellationToken).ConfigureAwait(true);
                break;

            case ShellShortcut.SelectTab:
                await ActivateIfAnyAsync(Tabs.ByNumber(tabNumber), cancellationToken).ConfigureAwait(true);
                break;

            case ShellShortcut.None:
            default:
                break;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.TerminalExited -= OnTerminalExited;
        Projects.Dispose();

        // Набор вкладок освобождается раньше моста: помпам нужно дождаться подтверждений страницы.
        await _workspace.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ActivateIfAnyAsync(TabViewModel? tab, CancellationToken cancellationToken)
    {
        if (tab is not null)
        {
            await ActivateTabAsync(tab, cancellationToken).ConfigureAwait(true);
        }
    }

    private void RefreshSessionCounts()
    {
        // Единственный источник счётчика — список вкладок. Отдельный счётчик на строке
        // разъезжается с реальностью, потому что вкладка закрывается тремя путями.
        foreach (var row in Projects.Rows)
        {
            row.SessionCount = Tabs.CountFor(row.Id);
        }

        RefreshCurrentProject();
    }

    private void RefreshCurrentProject()
    {
        var activeProjectId = Tabs.ActiveTab?.ProjectId;
        foreach (var row in Projects.Rows)
        {
            row.IsCurrent = activeProjectId == row.Id;
        }

        Raise(nameof(ActiveProjectRow));
    }

    private void OnTerminalExited(object? sender, TerminalExitedEventArgs e)
    {
        // Вкладка остаётся на экране: закрыть её может только пользователь (раздел 5.1 ТЗ).
        // Событие приходит из фонового потока помпы — переводим его в поток интерфейса.
        _dispatcher.Post(() =>
        {
            if (Tabs.Find(e.TerminalId) is { } tab)
            {
                tab.IsRunning = false;
            }
        });
    }

    private void ReportError(Exception exception) =>
        _prompt.ShowError("Ошибка", exception.Message);
}
