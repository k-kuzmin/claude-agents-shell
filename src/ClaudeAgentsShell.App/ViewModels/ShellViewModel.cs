using System.ComponentModel;
using System.Windows.Input;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Корневая ViewModel окна: связывает панель проектов и полосу вкладок с набором терминалов.
/// Весь разговор с миром идёт через порты — ни файлов, ни процессов, ни WebView2 здесь нет.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IAsyncDisposable, ITabStateSink
{
    private readonly ITerminalWorkspace _workspace;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;
    private readonly SessionStateCoordinator _sessionState;

    private bool _terminalPageReady;
    private bool _disposed;

    /// <inheritdoc cref="ShellViewModel" />
    public ShellViewModel(
        ITerminalWorkspace workspace,
        ProjectListViewModel projects,
        IUserPrompt prompt,
        IUiDispatcher dispatcher,
        SessionStateCoordinator sessionState)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(sessionState);

        _workspace = workspace;
        _prompt = prompt;
        _dispatcher = dispatcher;
        _sessionState = sessionState;

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

        // Окно истории сессий — этап M3, окно настроек — M5. Кнопки на своих местах,
        // но пока отключены.
        ShowHistoryCommand = new RelayCommand(static _ => { }, static _ => false);
        ShowSettingsCommand = new RelayCommand(static _ => { }, static _ => false);

        NewSessionCommand = new AsyncRelayCommand(
            _ => OpenSessionInActiveProjectAsync(CancellationToken.None),
            _ => ActiveProjectRow is { IsAvailable: true },
            ReportError);
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
        ShowAwaitingTabCommand = new AsyncRelayCommand(
            _ => ShowAwaitingTabAsync(CancellationToken.None),
            _ => Tabs.HasAwaitingInput,
            ReportError);

        _workspace.TerminalExited += OnTerminalExited;
        Tabs.PropertyChanged += OnTabsPropertyChanged;
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

    /// <summary>Настройки приложения. Отключены до этапа M5.</summary>
    public ICommand ShowSettingsCommand { get; }

    /// <summary>Новая сессия в активном проекте (кнопка «плюс» в полосе вкладок).</summary>
    public ICommand NewSessionCommand { get; }

    /// <summary>Переключиться на вкладку.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Закрыть вкладку.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>
    /// Клик по счётчику «N ждёт ввода»: показать первую ждущую вкладку (раздел 6.3 ТЗ).
    /// Ждущих вкладок нет — команда недоступна, а сам счётчик в разметке скрыт.
    /// </summary>
    public ICommand ShowAwaitingTabCommand { get; }

    /// <summary>
    /// Выбранный проект: его вкладки показаны в полосе и в нём откроется новая сессия.
    /// Выбор всегда явный — клик по строке, открытие вкладки или переход на вкладку.
    /// <c>null</c> только на холодном старте, пока пользователь ничего не выбрал.
    /// </summary>
    public ProjectRowViewModel? ActiveProjectRow => Projects.SelectedRow;

    /// <summary>
    /// В области терминала показан терминал, а не заглушка. Ровно один WebView2 на окно
    /// прячется целиком: показывать терминал чужого проекта, когда у выбранного вкладок нет,
    /// значило бы обманывать пользователя.
    /// <para>
    /// Пока страница терминалов не поднялась, область остаётся показанной, даже если
    /// показывать ещё нечего: WebView2 — дочерний HWND, и он создаётся, когда впервые
    /// получает место в разметке. Спрятав его до первого показа, мы рисковали бы
    /// не дождаться готовности страницы вообще.
    /// </para>
    /// </summary>
    public bool IsTerminalVisible => !_terminalPageReady || Tabs.ActiveTab is not null;

    /// <summary>Подсказка на месте терминала, когда показывать нечего.</summary>
    public string TerminalPlaceholderText => ActiveProjectRow switch
    {
        null => "Выберите проект слева или добавьте новый.",
        { IsAvailable: false } row => $"Каталог проекта «{row.Name}» недоступен: {row.Path}",
        { } row => $"В проекте «{row.Name}» нет открытых сессий.\n"
            + "Нажмите + в полосе вкладок или Ctrl+Shift+T.",
    };

    /// <summary>Поднимает страницу терминалов и читает список проектов.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _workspace.StartAsync(cancellationToken).ConfigureAwait(true);

        // Страница поднялась, её HWND создан — теперь область терминала можно прятать
        // под заглушку, не рискуя готовностью самой страницы.
        _terminalPageReady = true;

        // До первой вкладки: адрес приёмника хуков нужен файлу настроек, который уходит
        // сессии через --settings. Позже — и первая сессия осталась бы без маркера состояния.
        await _sessionState.StartAsync(this, cancellationToken).ConfigureAwait(true);

        await Projects.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Список строк перечитан: полоса вкладок не должна остаться на проекте,
        // строки которого в новом списке может уже не быть.
        SelectProject(null);
        RefreshSessionCounts();
    }

    /// <summary>Добавляет проект через диалог выбора папки.</summary>
    public async Task AddProjectAsync(CancellationToken cancellationToken)
    {
        var row = await Projects.AddProjectAsync(cancellationToken).ConfigureAwait(true);
        if (row is not null)
        {
            // Сразу после добавления «+» в полосе вкладок должен работать, а полоса —
            // показывать вкладки нового проекта, то есть быть пустой.
            SelectProject(row);
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

        if (!await Projects.RefreshAvailabilityAsync(row, cancellationToken).ConfigureAwait(true))
        {
            _prompt.ShowError(
                "Каталог недоступен",
                $"Каталог проекта «{row.Name}» недоступен: {row.Path}");
            RefreshCurrentProject();
            return null;
        }

        TerminalId terminalId;
        try
        {
            terminalId = await _workspace
                .OpenAsync(row.Project, new SessionLaunch.NewSession(), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (ShellNotFoundException exception)
        {
            // Единственный сбой, который приходит сюда: оболочки в системе нет.
            // Псевдоконсоль поднимается уже после возврата из OpenAsync, и её сбой
            // прилетает событием TerminalExited с ненулевым кодом — вкладка к тому моменту
            // уже в полосе и просто перестаёт считаться живой.
            _prompt.ShowError("Не удалось открыть сессию", exception.Message);
            return null;
        }

        var tab = new TabViewModel(terminalId, row.Id, row.Name);

        // Проект выбирается до добавления вкладки: иначе новая вкладка легла бы в полосу
        // чужого проекта и тут же из неё исчезла.
        SelectProject(row);
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
    /// Клик по строке проекта: полоса переключается на вкладки этого проекта, а видимой
    /// становится та из них, на которой пользователь был последней. Вкладок нет — полоса
    /// пуста, на месте терминала заглушка. Вкладки других проектов при этом продолжают жить.
    /// </summary>
    public async Task ActivateProjectAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var tab = Tabs.ActiveTabFor(row.Id);
        SelectProject(row);

        if (tab is not null)
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

        // Переход на вкладку выбирает её проект: полоса обязана показывать ту вкладку,
        // которая видна в области терминала.
        SelectProject(Projects.Rows.FirstOrDefault(row => row.Id == tab.ProjectId));
        Tabs.SetActive(tab);
        RefreshCurrentProject();
    }

    /// <summary>
    /// Показывает первую вкладку, ждущую ввода. Ждущих вкладок нет — не делает ничего.
    /// <para>
    /// Счётчик считает вкладки всех проектов, а полоса показывает вкладки одного, поэтому
    /// переход идёт обычным <see cref="ActivateTabAsync"/>: он же переключает выбранный
    /// проект. Без этого ждущая вкладка чужого проекта осталась бы недостижимой — команда
    /// сработала бы, а на экране не изменилось бы ничего.
    /// </para>
    /// </summary>
    public Task ShowAwaitingTabAsync(CancellationToken cancellationToken) =>
        ActivateIfAnyAsync(Tabs.FirstAwaitingInput(), cancellationToken);

    /// <summary>
    /// Закрывает вкладку. Живой процесс — сначала подтверждение: отказ оставляет вкладку на месте.
    /// </summary>
    /// <returns><c>true</c>, если вкладка закрыта.</returns>
    public async Task<bool> CloseTabAsync(TabViewModel tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        // Проверка идёт по всем открытым вкладкам, а не по видимой полосе: иначе вкладка
        // невыбранного проекта считалась бы уже закрытой.
        if (!Tabs.Contains(tab))
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
        Tabs.PropertyChanged -= OnTabsPropertyChanged;
        Projects.Dispose();

        // Координатор снимается раньше набора вкладок: он подписан на его события.
        _sessionState.Dispose();

        // Набор вкладок освобождается раньше моста: помпам нужно дождаться подтверждений страницы.
        await _workspace.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Состояние вкладки не трогает выбранный проект: маркеры живут во всех полосах.</remarks>
    void ITabStateSink.SetState(TerminalId terminalId, TabState state)
    {
        if (Tabs.Find(terminalId) is { } tab)
        {
            tab.State = state;
        }
    }

    /// <inheritdoc />
    void ITabStateSink.SetShortTitle(TerminalId terminalId, string shortTitle)
    {
        if (!string.IsNullOrWhiteSpace(shortTitle) && Tabs.Find(terminalId) is { } tab)
        {
            tab.ShortTitle = shortTitle;
        }
    }

    /// <inheritdoc />
    void ITabStateSink.ResetShortTitle(TerminalId terminalId)
    {
        if (Tabs.Find(terminalId) is { } tab)
        {
            tab.ShortTitle = TabViewModel.NewSessionTitle;
        }
    }

    /// <inheritdoc />
    bool ITabStateSink.TryGetWorkingDirectory(TerminalId terminalId, out string workingDirectory)
    {
        workingDirectory = string.Empty;
        if (Tabs.Find(terminalId) is not { } tab)
        {
            return false;
        }

        foreach (var row in Projects.Rows)
        {
            if (row.Id == tab.ProjectId)
            {
                workingDirectory = row.Path;
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Единственное место, где меняется выбранный проект: выбор строки и содержимое полосы
    /// вкладок обязаны меняться вместе, иначе полоса покажет вкладки одного проекта,
    /// а подсветка — другой.
    /// </summary>
    private void SelectProject(ProjectRowViewModel? row)
    {
        Projects.Select(row);
        Tabs.ShowProject(row?.Id);
        RefreshCurrentProject();
    }

    private void RefreshCurrentProject()
    {
        var target = ActiveProjectRow;
        foreach (var row in Projects.Rows)
        {
            row.IsCurrent = ReferenceEquals(row, target);
        }

        Raise(nameof(ActiveProjectRow));
        Raise(nameof(IsTerminalVisible));
        Raise(nameof(TerminalPlaceholderText));
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

                // Маркер снимается вместе с процессом. `SessionEnd` от убитой оболочки не придёт,
                // а ввод у вкладки без помпы не рождается вовсе — значит вкладка, умершая
                // в «ждёт ввода», осталась бы с оранжевой точкой и в счётчике до конца сеанса,
                // и клик по счётчику вёл бы на мёртвый терминал (разделы 5.3 и 8 ТЗ).
                tab.State = TabState.Unknown;
            }
        });
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Доступность команд окна перепроверяется через CommandManager (см. RelayCommand).
        // Состояние вкладок меняют хуки, а не пользователь, поэтому сам по себе запрос
        // доступности не придёт: кнопка счётчика появилась бы видимой, но выключенной.
        if (e.PropertyName is nameof(TabStripViewModel.HasAwaitingInput))
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ReportError(Exception exception) =>
        _prompt.ShowError("Ошибка", exception.Message);
}
