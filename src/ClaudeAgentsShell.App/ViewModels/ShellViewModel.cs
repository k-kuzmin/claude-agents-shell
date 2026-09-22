using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using ClaudeAgentsShell.App.Diff;
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
public sealed class ShellViewModel : ObservableObject, IAsyncDisposable, ITabStateSink, IDiffTabs
{
    private readonly ITerminalWorkspace _workspace;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;
    private readonly SessionStateCoordinator _sessionState;
    private readonly ILayoutStore _layoutStore;
    private readonly LayoutRecorder _layoutRecorder;
    private readonly DiffCoordinator _diff;
    private readonly DiffStaleTracker _diffStale;

    // Записи раскладки проектов, чей каталог был недоступен на старте: вкладки не подняты,
    // но и терять их нельзя — снимок переносит их как есть, пока проект не станет доступен
    // и пользователь не откроет в нём вкладку (тогда их заменяют живые) или пока проект
    // не уберут из списка (тогда снимок их больше не видит).
    private readonly Dictionary<Guid, ProjectLayout> _deferredLayouts = [];

    private bool _terminalPageReady;
    private bool _disposed;

    /// <inheritdoc cref="ShellViewModel" />
    /// <param name="workspace">Набор терминалов на странице.</param>
    /// <param name="projects">Панель проектов.</param>
    /// <param name="prompt">Сообщения и подтверждения пользователю.</param>
    /// <param name="dispatcher">Поток интерфейса.</param>
    /// <param name="sessionState">Координатор состояний вкладок по хукам.</param>
    /// <param name="layoutStore">Раскладка окна: читается один раз на старте.</param>
    /// <param name="layoutRecorder">Запись раскладки по изменениям (issue #4).</param>
    /// <param name="diff">Панель diff вкладок (issue #5).</param>
    /// <param name="diffStale">Плашка «есть изменения» у открытой панели diff по хукам.</param>
    public ShellViewModel(
        ITerminalWorkspace workspace,
        ProjectListViewModel projects,
        IUserPrompt prompt,
        IUiDispatcher dispatcher,
        SessionStateCoordinator sessionState,
        ILayoutStore layoutStore,
        LayoutRecorder layoutRecorder,
        DiffCoordinator diff,
        DiffStaleTracker diffStale)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(sessionState);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(layoutRecorder);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(diffStale);

        _workspace = workspace;
        _prompt = prompt;
        _dispatcher = dispatcher;
        _sessionState = sessionState;
        _layoutStore = layoutStore;
        _layoutRecorder = layoutRecorder;
        _diff = diff;
        _diffStale = diffStale;

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

        // Окно истории сессий — этап M3. Кнопка на месте, но пока отключена.
        ShowHistoryCommand = new RelayCommand(static _ => { }, static _ => false);

        // У пунктов контекстного меню проверки доступности нет: параметр приезжает
        // привязкой к PlacementTarget и на первом вычислении может быть ещё не разрешён,
        // а проверка самого параметра погасила бы пункт на первом открытии меню.
        // Не строка — команда просто ничего не делает.
        OpenProjectFolderCommand = new AsyncRelayCommand(
            parameter => parameter is ProjectRowViewModel row
                ? OpenProjectFolderAsync(row, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);

        // Настройкам проверка доступности по карману: проверяется не параметр, а RowOf —
        // неразрешённая привязка оставляет выбранную строку, и пункт меню не гаснет.
        // Выключен он ровно тогда, когда до команды не доехала ни одна строка: проектов нет
        // вовсе либо холодный старт, где ни один ещё не выбран, а привязка не разрешилась.
        // В обоих случаях настраивать нечего. Ветка без параметра (берётся выбранная строка)
        // осталась после того, как из заголовка окна убрали кнопку настроек: в разметке
        // команду зовёт только контекстное меню строки, и оно всегда передаёт свою строку.
        ShowSettingsCommand = new AsyncRelayCommand(
            parameter => RowOf(parameter) is { } row
                ? ShowProjectSettingsAsync(row, CancellationToken.None)
                : Task.CompletedTask,
            parameter => RowOf(parameter) is not null,
            ReportError);
        RemoveProjectCommand = new AsyncRelayCommand(
            parameter => parameter is ProjectRowViewModel row
                ? RemoveProjectAsync(row, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);

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
        RestartTabCommand = new AsyncRelayCommand(
            parameter => parameter is TabViewModel tab
                ? RestartTabAsync(tab, CancellationToken.None)
                : Task.CompletedTask,
            parameter => parameter is TabViewModel { HasExited: true },
            ReportError);
        ShowDiffCommand = new AsyncRelayCommand(
            parameter => parameter is TabViewModel tab
                ? ShowDiffAsync(tab, CancellationToken.None)
                : Task.CompletedTask,
            onError: ReportError);
        ShowAwaitingTabCommand = new AsyncRelayCommand(
            _ => ShowAwaitingTabAsync(CancellationToken.None),
            _ => Tabs.HasAwaitingInput,
            ReportError);

        _workspace.TerminalExited += OnTerminalExited;
        Tabs.PropertyChanged += OnTabsPropertyChanged;

        // Перестановка вкладок мышью идёт мимо ViewModel окна — прямо в полосу; видна она
        // только как перемещение в видимой коллекции.
        ((INotifyCollectionChanged)Tabs.Tabs).CollectionChanged += OnVisibleTabsChanged;
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

    /// <summary>Открыть каталог проекта в проводнике. Параметр — строка панели проектов.</summary>
    public ICommand OpenProjectFolderCommand { get; }

    /// <summary>
    /// Настройки проекта (раздел 6.5 ТЗ). Параметр — строка панели проектов; без параметра
    /// открываются настройки выбранного проекта, потому что кнопка в заголовке окна
    /// параметра не передаёт.
    /// </summary>
    public ICommand ShowSettingsCommand { get; }

    /// <summary>Убрать проект из списка. Параметр — строка панели проектов.</summary>
    public ICommand RemoveProjectCommand { get; }

    /// <summary>Новая сессия в активном проекте (кнопка «плюс» в полосе вкладок).</summary>
    public ICommand NewSessionCommand { get; }

    /// <summary>Переключиться на вкладку.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Закрыть вкладку.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>
    /// Перезапустить упавшую сессию (раздел 8 ТЗ). Параметр — вкладка, процесс которой
    /// завершился; на живой вкладке команда недоступна, а кнопки в разметке не видно.
    /// </summary>
    public ICommand RestartTabCommand { get; }

    /// <summary>
    /// Клик по счётчику «N ждёт ввода»: показать первую ждущую вкладку (раздел 6.3 ТЗ).
    /// Ждущих вкладок нет — команда недоступна, а сам счётчик в разметке скрыт.
    /// </summary>
    public ICommand ShowAwaitingTabCommand { get; }

    /// <summary>Кнопка diff на вкладке (issue #5): панель diff этой вкладки.</summary>
    public ICommand ShowDiffCommand { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Поднимается на каждом пути, которым вкладка уходит из набора: закрытие пользователем
    /// и закрытие вместе с проектом. Перезапуск вкладку не убирает — мёртвая остаётся рядом
    /// с новой, и её панель diff живёт, пока пользователь не закроет саму вкладку.
    /// </remarks>
    public event EventHandler<DiffTabClosedEventArgs>? TabClosed;

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

    /// <summary>
    /// Поднимает страницу терминалов, читает список проектов и восстанавливает вкладки
    /// прошлого запуска (issue #4). Запись раскладки включается только после восстановления:
    /// частично поднятая раскладка не должна затереть сохранённую.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _workspace.StartAsync(cancellationToken).ConfigureAwait(true);

        // Страница поднялась, её HWND создан — теперь область терминала можно прятать
        // под заглушку, не рискуя готовностью самой страницы.
        _terminalPageReady = true;

        // До первой вкладки: адрес приёмника хуков нужен файлу настроек, который уходит
        // сессии через --settings. Позже — и первая сессия осталась бы без маркера состояния.
        await _sessionState.StartAsync(this, cancellationToken).ConfigureAwait(true);

        // Тоже до первой вкладки: show_diff восстановленной сессии не должен ответить
        // «вкладка не найдена», а её первая пачка инструментов — пройти мимо плашки.
        _diff.Start(this);
        _diffStale.Start();

        await Projects.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Список строк перечитан: полоса вкладок не должна остаться на проекте,
        // строки которого в новом списке может уже не быть.
        SelectProject(null);
        RefreshProjectRows();

        var layout = await _layoutStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        await RestoreLayoutAsync(layout, cancellationToken).ConfigureAwait(true);
        _layoutRecorder.Start(CaptureLayout);
    }

    /// <summary>
    /// Закрытие окна, до гашения псевдоконсолей: отложенная запись раскладки уходит на диск
    /// по текущему снимку, и запись замораживается. Иначе гашение, которое шлёт выход
    /// оболочек и <c>SessionEnd</c>, записало бы последней пустую раскладку.
    /// </summary>
    public Task PersistLayoutAndFreezeAsync(CancellationToken cancellationToken) =>
        _layoutRecorder.FlushAndFreezeAsync(cancellationToken);

    /// <summary>Добавляет проект: выбор папки, затем диалог настроек (раздел 6.5 ТЗ).</summary>
    public async Task AddProjectAsync(CancellationToken cancellationToken)
    {
        var row = await Projects.AddProjectAsync(cancellationToken).ConfigureAwait(true);
        if (row is not null)
        {
            // Сразу после добавления «+» в полосе вкладок должен работать, а полоса —
            // показывать вкладки нового проекта, то есть быть пустой.
            SelectProject(row);
            RefreshProjectRows();
        }
    }

    /// <summary>
    /// Открывает новую сессию в проекте и делает её вкладку активной.
    /// Каталог проекта исчез — запуск заблокирован, строка помечена недоступной (раздел 8 ТЗ).
    /// </summary>
    /// <returns>Открытая вкладка либо <c>null</c>, если запуск не состоялся.</returns>
    public Task<TabViewModel?> OpenSessionAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        return OpenTabAsync(row, new SessionLaunch.NewSession(), shortTitle: null, reportFailures: true, cancellationToken);
    }

    /// <summary>
    /// Общий путь открытия вкладки: и для новой сессии, и для восстановления раскладки.
    /// </summary>
    /// <param name="row">Проект вкладки.</param>
    /// <param name="launch">Режим запуска <c>claude</c>.</param>
    /// <param name="shortTitle">Короткое имя, известное заранее (из раскладки); <c>null</c> — «новая сессия».</param>
    /// <param name="reportFailures">
    /// Показывать ли сбой пользователю. Восстановление молчит: вкладка недоступного проекта
    /// просто пропускается, а не встречает пользователя пачкой диалогов на старте.
    /// </param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task<TabViewModel?> OpenTabAsync(
        ProjectRowViewModel row,
        SessionLaunch launch,
        string? shortTitle,
        bool reportFailures,
        CancellationToken cancellationToken)
    {
        if (!await Projects.RefreshAvailabilityAsync(row, cancellationToken).ConfigureAwait(true))
        {
            if (reportFailures)
            {
                _prompt.ShowError(
                    "Каталог недоступен",
                    $"Каталог проекта «{row.Name}» недоступен: {row.Path}");
            }

            RefreshCurrentProject();
            return null;
        }

        // Каталог запоминается до запуска: вкладка обязана помнить каталог, в котором её
        // сессия действительно стартовала, а не тот, который окажется у проекта потом.
        var workingDirectory = row.Path;

        TerminalId terminalId;
        try
        {
            terminalId = await _workspace
                .OpenAsync(row.Project, launch, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (ShellNotFoundException exception)
        {
            // Единственный сбой, который приходит сюда: оболочки в системе нет.
            // Псевдоконсоль поднимается уже после возврата из OpenAsync, и её сбой
            // прилетает событием TerminalExited с ненулевым кодом — вкладка к тому моменту
            // уже в полосе и просто перестаёт считаться живой.
            if (reportFailures)
            {
                _prompt.ShowError("Не удалось открыть сессию", exception.Message);
            }

            return null;
        }

        // Живая вкладка заменяет перенесённые записи проекта: иначе после возврата каталога
        // следующий запуск поднял бы и их, и новые — дублями.
        _deferredLayouts.Remove(row.Id);

        var tab = new TabViewModel(terminalId, row.Id, row.Name, workingDirectory)
        {
            // Сессия известна с запуска: до прихода SessionStart раскладка иначе записала бы
            // вкладке sessionId: null, и следующий запуск поднял бы её новой сессией.
            SessionId = launch is SessionLaunch.ResumeSession resume ? resume.SessionId : null,
        };

        // Имя из раскладки ставится сразу, чтобы вкладка не висела «новой сессией», пока
        // координатор не вычитает заголовок из транскрипта.
        if (!string.IsNullOrWhiteSpace(shortTitle))
        {
            tab.ShortTitle = shortTitle;
        }

        // Проект выбирается до добавления вкладки: иначе новая вкладка легла бы в полосу
        // чужого проекта и тут же из неё исчезла.
        SelectProject(row);
        Tabs.Add(tab);

        // Открытую вкладку страница показывает сама внутри OpenAsync — второй показ
        // был бы лишним разговором с мостом. Здесь остаётся только состояние ViewModel.
        Tabs.SetActive(tab);
        RefreshProjectRows();
        return tab;
    }

    /// <summary>
    /// Открывает каталог проекта в проводнике. Не открылся — сообщение пользователю:
    /// контекстное меню не должно ронять окно.
    /// </summary>
    public async Task OpenProjectFolderAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!await Projects.OpenFolderAsync(row, cancellationToken).ConfigureAwait(true))
        {
            _prompt.ShowError(
                "Не удалось открыть папку",
                $"Каталог проекта «{row.Name}» не открылся в проводнике: {row.Path}");
        }
    }

    /// <summary>
    /// Показывает настройки проекта и, если пользователь их сохранил, обновляет строку
    /// и заголовки уже открытых вкладок этого проекта.
    /// </summary>
    /// <remarks>
    /// Имя и путь ведут себя по-разному, потому что это разные вещи. Имя — отображаемое:
    /// оно доезжает до вкладок сразу, иначе панель показывала бы новое имя проекта, а его же
    /// вкладки — старое (раздел 6.3 ТЗ). Путь — рабочий: перенести работающую псевдоконсоль
    /// в другой каталог нельзя, поэтому открытые вкладки остаются в том каталоге, в котором
    /// стартовали (см. <see cref="TabViewModel.WorkingDirectory" />), а новый путь достаётся
    /// только сессиям, открытым после переноса.
    /// </remarks>
    public async Task ShowProjectSettingsAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (await Projects.EditProjectAsync(row, cancellationToken).ConfigureAwait(true))
        {
            // Имя проекта живёт и в заголовке вкладки: без этого прохода вкладка до конца
            // своей жизни показывала бы имя, которого у проекта уже нет.
            foreach (var tab in Tabs.AllTabs)
            {
                if (tab.ProjectId == row.Id)
                {
                    tab.ProjectName = row.Name;
                }
            }

            // Имя, путь и доступность могли измениться: заглушка на месте терминала и
            // подсветка строки читают их у выбранного проекта.
            RefreshCurrentProject();
        }
    }

    /// <summary>
    /// Убирает проект из списка после подтверждения. Каталог на диске остаётся на месте:
    /// приложение в проект пользователя ничего не пишет и ничего из него не удаляет.
    /// <para>
    /// Открытые сессии этого проекта закрываются вместе со строкой. Оставить их в живых
    /// нельзя: полоса показывает вкладки выбранного проекта, а выбрать исчезнувшую строку
    /// уже нечем — вкладки стали бы недостижимыми, продолжая держать псевдоконсоли.
    /// Поэтому число сессий названо прямо в подтверждении, а не спрошено по одной.
    /// </para>
    /// </summary>
    /// <returns><c>true</c>, если проект убран.</returns>
    public async Task<bool> RemoveProjectAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!Projects.Rows.Contains(row))
        {
            // Строку уже убрали: повторный вызов приходит от меню, открытого до предыдущего
            // подтверждения.
            return false;
        }

        // Список материализуется до удаления: закрытие вкладки меняет тот самый набор,
        // по которому идёт перебор.
        var live = Tabs.AllTabs.Where(tab => tab.ProjectId == row.Id).ToList();

        var message = live.Count == 0
            ? $"Убрать проект «{row.Name}» из списка? Каталог на диске останется на месте."
            : $"Убрать проект «{row.Name}» из списка? Его открытых сессий: {live.Count} — "
                + "они будут закрыты. Каталог на диске останется на месте.";

        if (!_prompt.Confirm("Убрать проект", message))
        {
            return false;
        }

        var wasSelected = ReferenceEquals(ActiveProjectRow, row);

        // Сначала список: не удалось его записать — пользователь не лишится ни строки,
        // ни открытых сессий, а исключение доедет до сообщения об ошибке.
        if (!await Projects.RemoveProjectAsync(row, cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        // Убранный проект не поднимется никогда — его перенесённые записи больше не нужны.
        _deferredLayouts.Remove(row.Id);

        // Закрытие идёт разом по всем вкладкам, а не по одной: у каждой свой бюджет ожидания
        // выхода процесса, и последовательный проход умножал бы его на число вкладок — строка
        // проекта уже исчезла, а её вкладки гасли бы по одной ещё десятки секунд.
        try
        {
            var closing = live
                .Select(tab => _workspace.CloseAsync(tab.TerminalId, cancellationToken))
                .ToList();

            await Task.WhenAll(closing).ConfigureAwait(true);
        }
        finally
        {
            // Полоса приводится в порядок при любом исходе. Сорвавшееся закрытие — не повод
            // оставить вкладку на экране: строки её проекта в списке уже нет, выбрать эту
            // вкладку стало нечем, а это ровно то недостижимое состояние, от которого
            // закрытие вкладок вместе со строкой и должно уберечь.
            foreach (var tab in live)
            {
                Tabs.Remove(tab);
                RaiseTabClosed(tab);
            }

            // Активная вкладка могла быть среди закрытых, а удержать выбор не на чем:
            // соседней вкладки в убранном проекте не осталось. Вкладку другого проекта
            // проверка не трогает — она в наборе, и выбор с неё не снимается.
            if (Tabs.ActiveTab is { } active && !Tabs.Contains(active))
            {
                Tabs.SetActive(null);
            }

            if (wasSelected)
            {
                // Выбранной строки больше нет: полоса остаётся ни на чём, на месте терминала —
                // заглушка. Вкладки других проектов при этом продолжают жить.
                SelectProject(null);
            }

            RefreshProjectRows();
        }

        return true;
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
        RaiseTabClosed(tab);
        RefreshProjectRows();

        if (next is null)
        {
            Tabs.SetActive(null);
            RefreshCurrentProject();
            return true;
        }

        await ActivateTabAsync(next, cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Перезапускает упавшую сессию: открывает новую сессию в том же проекте (раздел 8 ТЗ).
    /// Каталог проекта исчез — запуск заблокирован тем же способом, что и обычное открытие,
    /// и мёртвая вкладка остаётся на месте.
    /// </summary>
    /// <param name="tab">Вкладка, процесс которой завершился.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Вкладка новой сессии либо <c>null</c>, если запуск не состоялся.</returns>
    /// <remarks>
    /// Мёртвая вкладка **не заменяется**: на её экране остаётся вывод, ради которого она и
    /// живёт после выхода процесса — в том числе сообщение об ошибке, которое пользователь
    /// ещё не прочитал. Поэтому новая сессия открывается соседней вкладкой, а мёртвую
    /// пользователь закрывает крестиком, когда прочтёт. Соседняя встаёт в конец полосы:
    /// вставка рядом с мёртвой потребовала бы отдельной операции у набора вкладок, а порядок
    /// полосы принадлежит пользователю (перетаскивание, раздел 6.3 ТЗ).
    /// </remarks>
    public async Task<TabViewModel?> RestartTabAsync(TabViewModel tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.IsRunning)
        {
            // Живую сессию перезапускать нечего: её процесс на месте.
            return null;
        }

        var row = Projects.Rows.FirstOrDefault(candidate => candidate.Id == tab.ProjectId);
        if (row is null)
        {
            // Строку проекта убрали из списка, пока вкладка лежала мёртвой: запускать не в чем.
            return null;
        }

        return await OpenSessionAsync(row, cancellationToken).ConfigureAwait(true);
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

            case ShellShortcut.ShowDiff:
                if (Tabs.ActiveTab is { } current)
                {
                    await _diff.OpenForTabAsync(current.TerminalId, cancellationToken).ConfigureAwait(true);
                }

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

        // Первым делом: гашение ниже шлёт выход оболочек, и раскладка не должна его записать.
        // Окно замораживает запись ещё раньше (PersistLayoutAndFreezeAsync); это страховка
        // на случай освобождения мимо окна.
        _layoutRecorder.Freeze();

        _workspace.TerminalExited -= OnTerminalExited;
        Tabs.PropertyChanged -= OnTabsPropertyChanged;
        ((INotifyCollectionChanged)Tabs.Tabs).CollectionChanged -= OnVisibleTabsChanged;
        Projects.Dispose();

        // Координатор снимается раньше набора вкладок: он подписан на его TerminalExited.
        _sessionState.Dispose();

        // Diff гасится до набора вкладок и моста: его построения ещё шлют на страницу
        // оглавления и файлы. Сначала источник сигналов «устарело», затем сам координатор —
        // он отменяет и дожидается своих работ. Продолжение возвращается в поток интерфейса:
        // набор вкладок ниже освобождается оттуда же, как и без diff.
        _diffStale.Dispose();
        await _diff.DisposeAsync().ConfigureAwait(true);

        // Набор вкладок освобождается раньше моста: помпам нужно дождаться подтверждений страницы.
        await _workspace.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    bool IDiffTabs.TryResolveTerminal(string? correlationToken, out TerminalId terminalId) =>
        _workspace.TryResolveTerminal(correlationToken, out terminalId);

    /// <inheritdoc />
    TabViewModel? IDiffTabs.Find(TerminalId terminalId) => Tabs.Find(terminalId);

    /// <summary>
    /// Кнопка diff на вкладке: вкладка становится активной, затем открывается её панель —
    /// страница показывает панель поверх терминала активной вкладки.
    /// </summary>
    public async Task ShowDiffAsync(TabViewModel tab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!Tabs.Contains(tab))
        {
            return;
        }

        await ActivateTabAsync(tab, cancellationToken).ConfigureAwait(true);
        await _diff.OpenForTabAsync(tab.TerminalId, cancellationToken).ConfigureAwait(true);
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
            SetShortTitle(tab, shortTitle);
        }
    }

    /// <inheritdoc />
    void ITabStateSink.ResetShortTitle(TerminalId terminalId)
    {
        if (Tabs.Find(terminalId) is { } tab)
        {
            SetShortTitle(tab, TabViewModel.NewSessionTitle);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Отдаётся каталог самой вкладки, а не текущий путь её проекта: проект могли перенести,
    /// а сессия осталась работать там, где стартовала. Новый путь увёл бы координатор за
    /// транскриптом в чужой slug, и вкладка молча осталась бы «новой сессией» навсегда.
    /// </remarks>
    bool ITabStateSink.TryGetWorkingDirectory(TerminalId terminalId, out string workingDirectory)
    {
        if (Tabs.Find(terminalId) is { } tab)
        {
            workingDirectory = tab.WorkingDirectory;
            return true;
        }

        workingDirectory = string.Empty;
        return false;
    }

    /// <inheritdoc />
    void ITabStateSink.SetSessionContext(TerminalId terminalId, string? sessionId, string? currentDirectory, bool? sessionEnded)
    {
        if (Tabs.Find(terminalId) is not { } tab)
        {
            return;
        }

        // Метод зовётся на каждом хуке главного агента с теми же значениями, поэтому раскладка
        // дёргается только на настоящей смене сессии или её конца, а не на каждом шаге работы.
        var layoutChanged = false;

        if (!string.IsNullOrWhiteSpace(sessionId) && tab.SessionId != sessionId)
        {
            tab.SessionId = sessionId;
            layoutChanged = true;
        }

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            tab.CurrentDirectory = currentDirectory;
        }

        if (sessionEnded is { } ended && tab.SessionEnded != ended)
        {
            tab.SessionEnded = ended;
            layoutChanged = true;
        }

        if (layoutChanged)
        {
            _layoutRecorder.Signal();
        }
    }

    private void SetShortTitle(TabViewModel tab, string shortTitle)
    {
        if (tab.ShortTitle != shortTitle)
        {
            tab.ShortTitle = shortTitle;
            _layoutRecorder.Signal();
        }
    }

    /// <summary>
    /// Поднимает вкладки сохранённой раскладки: последовательно, в сохранённом порядке, затем
    /// активные вкладки проектов и активный проект. Проекта больше нет — его вкладки
    /// отбрасываются молча. Каталог проекта недоступен — вкладки не поднимаются, но их записи
    /// сохраняются в раскладке до возврата каталога.
    /// </summary>
    private async Task RestoreLayoutAsync(WorkspaceLayout layout, CancellationToken cancellationToken)
    {
        var chosen = new List<TabViewModel>(layout.Projects.Count);

        foreach (var project in layout.Projects)
        {
            var row = Projects.Rows.FirstOrDefault(candidate => candidate.Id == project.ProjectId);
            if (row is null)
            {
                continue;
            }

            if (!await Projects.RefreshAvailabilityAsync(row, cancellationToken).ConfigureAwait(true))
            {
                _deferredLayouts[row.Id] = project;
                continue;
            }

            TabViewModel? first = null;
            TabViewModel? active = null;

            for (var index = 0; index < project.Tabs.Count; index++)
            {
                var saved = project.Tabs[index];
                SessionLaunch launch = saved.SessionId is { } sessionId
                    ? new SessionLaunch.ResumeSession(sessionId)
                    : new SessionLaunch.NewSession();

                var tab = await OpenTabAsync(row, launch, saved.ShortTitle, reportFailures: false, cancellationToken)
                    .ConfigureAwait(true);
                if (tab is null)
                {
                    continue;
                }

                first ??= tab;

                // Индекс активной — позиция в сохранённом списке, а не среди поднятых.
                if (index == project.ActiveTabIndex)
                {
                    active = tab;
                }
            }

            if ((active ?? first) is { } remembered)
            {
                chosen.Add(remembered);
            }
        }

        // Запоминание активной вкладки каждого проекта: возврат к проекту приведёт на неё.
        foreach (var tab in chosen)
        {
            Tabs.SetActive(tab);
        }

        var target = Projects.Rows.FirstOrDefault(row => row.Id == layout.ActiveProjectId)
            ?? (chosen.Count > 0 ? Projects.Rows.FirstOrDefault(row => row.Id == chosen[0].ProjectId) : null);
        if (target is null)
        {
            return;
        }

        SelectProject(target);

        // Показ именно принудительный, а не через ActivateTabAsync: последней на странице
        // показана последняя поднятая вкладка, а полоса уже может считать нужную активной —
        // и ActivateTabAsync тогда не сказал бы странице ничего.
        if (Tabs.ActiveTabFor(target.Id) is { } visible)
        {
            await _workspace.ActivateAsync(visible.TerminalId, cancellationToken).ConfigureAwait(true);
            Tabs.SetActive(visible);
        }
        else
        {
            Tabs.SetActive(null);
        }

        RefreshProjectRows();
    }

    /// <summary>
    /// Снимок раскладки для записи. Живость считается здесь, в момент записи: в раскладку идут
    /// только вкладки, у которых работает оболочка и сессия не закончилась. У проекта без живых
    /// вкладок, чей каталог был недоступен на старте, переносятся его прежние записи.
    /// </summary>
    private WorkspaceLayout CaptureLayout()
    {
        var projects = new List<ProjectLayout>();

        foreach (var row in Projects.Rows)
        {
            var live = Tabs.AllTabs
                .Where(tab => tab.ProjectId == row.Id && tab.IsRunning && !tab.SessionEnded)
                .ToList();
            if (live.Count == 0)
            {
                if (_deferredLayouts.TryGetValue(row.Id, out var deferred))
                {
                    projects.Add(deferred);
                }

                continue;
            }

            var activeIndex = Tabs.ActiveTabFor(row.Id) is { } active ? live.IndexOf(active) : 0;

            projects.Add(new ProjectLayout(
                row.Id,
                Math.Max(activeIndex, 0),
                [.. live.Select(static tab => new TabLayout(
                    tab.SessionId,
                    tab.ShortTitle == TabViewModel.NewSessionTitle ? null : tab.ShortTitle))]));
        }

        return new WorkspaceLayout(ActiveProjectRow?.Id, projects);
    }

    // Строка, к которой относится команда: явно переданная либо выбранная. Кнопка настроек
    // в заголовке окна параметра не передаёт — там подразумевается выбранный проект.
    private ProjectRowViewModel? RowOf(object? parameter) =>
        parameter as ProjectRowViewModel ?? ActiveProjectRow;

    private async Task ActivateIfAnyAsync(TabViewModel? tab, CancellationToken cancellationToken)
    {
        if (tab is not null)
        {
            await ActivateTabAsync(tab, cancellationToken).ConfigureAwait(true);
        }
    }

    // Зовётся там, где изменился состав вкладок: и счётчик, и точка состояния строки
    // считаются по этому составу.
    private void RefreshProjectRows()
    {
        // Единственный источник счётчика — список вкладок. Отдельный счётчик на строке
        // разъезжается с реальностью, потому что вкладка закрывается тремя путями.
        foreach (var row in Projects.Rows)
        {
            row.SessionCount = Tabs.CountFor(row.Id);
            row.MarkerState = Tabs.MarkerStateFor(row.Id);
        }

        // Состав вкладок изменился — в том числе у невыбранного проекта, чьё закрытие
        // видимую полосу не трогает.
        _layoutRecorder.Signal();
        RefreshCurrentProject();
    }

    // Отдельно от счётчиков: состояние меняют хуки, и на каждое их событие поднимать
    // ещё и свойства выбранного проекта (RefreshCurrentProject) значило бы дёргать окно
    // там, где поменялся цвет одной точки.
    private void RefreshMarkerStates()
    {
        foreach (var row in Projects.Rows)
        {
            row.MarkerState = Tabs.MarkerStateFor(row.Id);
        }
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
        _layoutRecorder.Signal();
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
                // Пометка о коде выхода (раздел 8 ТЗ): код приходит вместе с событием,
                // разбирать ради него вывод вкладки не нужно и запрещено.
                tab.MarkExited(e.ExitCode);

                // Вкладка с умершей оболочкой из раскладки выпадает.
                _layoutRecorder.Signal();

                // Процесс умер без жеста пользователя, поэтому сам реквери не придёт, а кнопка
                // «перезапустить» появилась бы на вкладке выключенной. Это та самая проверка
                // доступности «изменилось не по нажатию», о которой говорит RelayCommand,
                // и зовётся она только когда вкладка действительно нашлась и умерла.
                CommandManager.InvalidateRequerySuggested();
            }

            // Маркер с умершей вкладки снимает координатор состояний: он подписан на то же
            // событие. Выставлять состояние здесь нельзя — источник состояния один
            // (раздел 7 CLAUDE.md), и второй писатель развёл бы показанное с запомненным.
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

        // Хук поменял состояние уже открытой вкладки: состав вкладок тот же, счётчики
        // на строках не поедут, а точка проекта обязана перекраситься. Подписка на вкладки
        // остаётся в полосе — сюда приходит только признак «маркеры устарели».
        if (e.PropertyName is nameof(TabStripViewModel.StateRevision))
        {
            RefreshMarkerStates();
        }

        if (e.PropertyName is nameof(TabStripViewModel.ActiveTab))
        {
            _layoutRecorder.Signal();
        }
    }

    // Добавление, закрытие, перестановка мышью и смена проекта в видимой полосе.
    private void OnVisibleTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _layoutRecorder.Signal();

    private void RaiseTabClosed(TabViewModel tab) =>
        TabClosed?.Invoke(this, new DiffTabClosedEventArgs(tab.TerminalId));

    private void ReportError(Exception exception) =>
        _prompt.ShowError("Ошибка", exception.Message);
}
