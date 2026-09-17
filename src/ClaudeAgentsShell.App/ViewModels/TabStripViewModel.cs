using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Полоса вкладок: список, активная вкладка и счётчик «N ждёт ввода».
/// Ничего не запускает и не закрывает — этим занимается <see cref="ShellViewModel"/>.
/// <para>
/// Полоса показывает вкладки **одного** проекта — выбранного в панели проектов
/// (решение пользователя поверх раздела 6.3 ТЗ, где полоса общая). Вкладки остальных
/// проектов никуда не деваются: их псевдоконсоли работают, вывод копится, и при возврате
/// к проекту всё на месте. Меняется только то, какие вкладки показаны.
/// </para>
/// </summary>
public sealed class TabStripViewModel : ObservableObject
{
    // Все открытые вкладки в порядке открытия: счётчики на строках проектов, поиск по
    // идентификатору терминала и гашение работают по этому списку, а не по видимой части.
    private readonly List<TabViewModel> _all = [];

    // Вкладки выбранного проекта — то, что видит пользователь и по чему ходят Ctrl+Tab
    // и Ctrl+1..9.
    private readonly ObservableCollection<TabViewModel> _visible = [];

    // Какая вкладка проекта была активной последней: возврат к проекту возвращает
    // пользователя именно туда, а не на первую попавшуюся вкладку.
    private readonly Dictionary<Guid, TabViewModel> _lastActiveByProject = [];

    private Guid? _projectId;
    private TabViewModel? _activeTab;
    private int _awaitingInputCount;

    /// <inheritdoc cref="TabStripViewModel" />
    public TabStripViewModel() => Tabs = new ReadOnlyObservableCollection<TabViewModel>(_visible);

    /// <summary>Вкладки выбранного проекта в порядке открытия — содержимое полосы.</summary>
    public ReadOnlyObservableCollection<TabViewModel> Tabs { get; }

    /// <summary>
    /// Все открытые вкладки, включая вкладки невыбранных проектов. Нужны для счётчиков
    /// на строках проектов и для поиска вкладки по идентификатору терминала.
    /// </summary>
    public IReadOnlyList<TabViewModel> AllTabs => _all;

    /// <summary>Активная вкладка выбранного проекта; <c>null</c>, если показывать нечего.</summary>
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                Raise(nameof(HasTabs));
            }
        }
    }

    /// <summary>В полосе есть хотя бы одна вкладка.</summary>
    public bool HasTabs => _visible.Count > 0;

    /// <summary>
    /// Сколько вкладок ждёт ввода. Считается по всем вкладкам, а не только по видимым:
    /// смысл счётчика — заметить, что тебя ждёт сессия, в том числе в другом проекте.
    /// Источник состояния — хуки (M4); до тех пор счётчик равен нулю и в разметке скрыт.
    /// Клик по счётчику (раздел 6.3 ТЗ) в M4 должен будет заодно переключать выбранный проект.
    /// </summary>
    public int AwaitingInputCount
    {
        get => _awaitingInputCount;
        private set
        {
            if (SetProperty(ref _awaitingInputCount, value))
            {
                Raise(nameof(HasAwaitingInput));
            }
        }
    }

    /// <summary>Счётчик показывается только когда есть кого считать.</summary>
    public bool HasAwaitingInput => AwaitingInputCount > 0;

    /// <summary>
    /// Переключает полосу на вкладки проекта. <c>null</c> — проект не выбран, полоса пуста.
    /// Ни одна вкладка при этом не закрывается: меняется только видимая часть.
    /// </summary>
    public void ShowProject(Guid? projectId)
    {
        if (_projectId == projectId)
        {
            return;
        }

        _projectId = projectId;
        Rebuild();
    }

    /// <summary>Добавляет вкладку в конец полосы.</summary>
    public void Add(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        tab.PropertyChanged += OnTabPropertyChanged;
        _all.Add(tab);

        if (IsVisible(tab))
        {
            _visible.Add(tab);
            Raise(nameof(HasTabs));
        }

        RecalculateAwaitingInput();
    }

    /// <summary>
    /// Убирает вкладку из полосы и, если она была активной, выбирает соседнюю в том же проекте.
    /// </summary>
    /// <returns>Вкладка, ставшая активной, либо <c>null</c>, если у проекта не осталось ни одной.</returns>
    public TabViewModel? Remove(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_all.Remove(tab))
        {
            return ActiveTab;
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);
        var index = _visible.IndexOf(tab);

        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.IsActive = false;

        if (index >= 0)
        {
            _visible.RemoveAt(index);
            Raise(nameof(HasTabs));
        }

        if (_lastActiveByProject.TryGetValue(tab.ProjectId, out var remembered)
            && ReferenceEquals(remembered, tab))
        {
            _lastActiveByProject.Remove(tab.ProjectId);
        }

        RecalculateAwaitingInput();

        if (!wasActive)
        {
            return ActiveTab;
        }

        if (index < 0 || _visible.Count == 0)
        {
            SetActive(null);
            return null;
        }

        // Соседняя слева, если закрыли последнюю в ряду, иначе вставшая на это место.
        return _visible[Math.Min(index, _visible.Count - 1)];
    }

    /// <summary>Делает вкладку активной; <c>null</c> снимает выделение со всех.</summary>
    public void SetActive(TabViewModel? tab)
    {
        // Выделение снимается со всех вкладок, а не только с видимых: вкладка чужого проекта
        // не должна вернуться из-под переключения всё ещё помеченной активной.
        foreach (var candidate in _all)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        if (tab is not null)
        {
            _lastActiveByProject[tab.ProjectId] = tab;
        }

        ActiveTab = tab;
    }

    /// <summary>
    /// Вкладка по идентификатору терминала среди **всех** открытых; <c>null</c>, если такой нет.
    /// Событие выхода процесса приходит и для вкладок невыбранных проектов.
    /// </summary>
    public TabViewModel? Find(TerminalId terminalId) =>
        _all.FirstOrDefault(tab => tab.TerminalId == terminalId);

    /// <summary>Открыта ли вкладка — среди всех проектов, а не только среди видимых.</summary>
    public bool Contains(TabViewModel tab) => _all.Contains(tab);

    /// <summary>Сколько вкладок открыто в проекте. Считается по всем вкладкам.</summary>
    public int CountFor(Guid projectId) => _all.Count(tab => tab.ProjectId == projectId);

    /// <summary>
    /// Активная вкладка проекта: последняя, на которой пользователь был, иначе первая открытая.
    /// <c>null</c>, если у проекта нет вкладок.
    /// </summary>
    public TabViewModel? ActiveTabFor(Guid projectId)
    {
        if (_lastActiveByProject.TryGetValue(projectId, out var remembered) && _all.Contains(remembered))
        {
            return remembered;
        }

        return _all.FirstOrDefault(tab => tab.ProjectId == projectId);
    }

    /// <summary>Вкладка полосы по номеру 1..9; <c>null</c>, если столько вкладок не показано.</summary>
    public TabViewModel? ByNumber(int number) =>
        number >= 1 && number <= _visible.Count ? _visible[number - 1] : null;

    /// <summary>Следующая вкладка полосы по кругу; <c>null</c>, если полоса пуста.</summary>
    public TabViewModel? Next() => Shift(1);

    /// <summary>Предыдущая вкладка полосы по кругу; <c>null</c>, если полоса пуста.</summary>
    public TabViewModel? Previous() => Shift(-1);

    private bool IsVisible(TabViewModel tab) => _projectId is { } id && tab.ProjectId == id;

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var tab in _all)
        {
            if (IsVisible(tab))
            {
                _visible.Add(tab);
            }
        }

        // Активная вкладка чужого проекта полосе больше не принадлежит: выбор активной
        // внутри нового проекта делает ShellViewModel — он же показывает её терминал.
        if (ActiveTab is { } active && !IsVisible(active))
        {
            SetActive(null);
        }

        Raise(nameof(HasTabs));
    }

    private TabViewModel? Shift(int delta)
    {
        if (_visible.Count == 0)
        {
            return null;
        }

        var current = ActiveTab is null ? -1 : _visible.IndexOf(ActiveTab);
        if (current < 0)
        {
            return _visible[0];
        }

        var index = (current + delta + _visible.Count) % _visible.Count;
        return _visible[index];
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.IsAwaitingInput))
        {
            RecalculateAwaitingInput();
        }
    }

    private void RecalculateAwaitingInput() =>
        AwaitingInputCount = _all.Count(tab => tab.IsAwaitingInput);
}
