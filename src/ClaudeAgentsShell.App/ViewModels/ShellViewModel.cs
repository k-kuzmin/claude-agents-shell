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
        // В обоих случаях настраивать нечего ни в меню, ни кнопкой в заголовке окна,
        // которая параметра не передаёт.
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

    /// <summary>Добавляет проект: выбор папки, затем диалог настроек (раздел 6.5 ТЗ).</summary>
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

        // Каталог запоминается до запуска: вкладка обязана помнить каталог, в котором её
        // сессия действительно стартовала, а не тот, который окажется у проекта потом.
        var workingDirectory = row.Path;

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

        var tab = new TabViewModel(terminalId, row.Id, row.Name, workingDirectory);

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

            RefreshSessionCounts();
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

        // Координатор снимается раньше набора вкладок: он подписан на его TerminalExited.
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
                // Пометка о коде выхода (раздел 8 ТЗ): код приходит вместе с событием,
                // разбирать ради него вывод вкладки не нужно и запрещено.
                tab.MarkExited(e.ExitCode);

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
    }

    private void ReportError(Exception exception) =>
        _prompt.ShowError("Ошибка", exception.Message);
}
