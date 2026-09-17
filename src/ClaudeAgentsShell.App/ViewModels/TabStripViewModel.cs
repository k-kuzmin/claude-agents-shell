using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Полоса вкладок: список, активная вкладка и счётчик «N ждёт ввода».
/// Ничего не запускает и не закрывает — этим занимается <see cref="ShellViewModel"/>.
/// </summary>
public sealed class TabStripViewModel : ObservableObject
{
    private readonly ObservableCollection<TabViewModel> _tabs = [];

    // Какая вкладка проекта была активной последней: клик по строке проекта возвращает
    // пользователя именно туда, а не на первую попавшуюся вкладку.
    private readonly Dictionary<Guid, TabViewModel> _lastActiveByProject = [];

    private TabViewModel? _activeTab;
    private int _awaitingInputCount;

    /// <inheritdoc cref="TabStripViewModel" />
    public TabStripViewModel() => Tabs = new ReadOnlyObservableCollection<TabViewModel>(_tabs);

    /// <summary>Открытые вкладки в порядке открытия.</summary>
    public ReadOnlyObservableCollection<TabViewModel> Tabs { get; }

    /// <summary>Активная вкладка; <c>null</c>, если открытых нет.</summary>
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

    /// <summary>Есть хотя бы одна вкладка.</summary>
    public bool HasTabs => _tabs.Count > 0;

    /// <summary>
    /// Сколько вкладок ждёт ввода. Источник — состояние вкладок, которое приходит от хуков (M4);
    /// до тех пор счётчик равен нулю и в разметке скрыт.
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

    /// <summary>Добавляет вкладку в конец полосы.</summary>
    public void Add(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        tab.PropertyChanged += OnTabPropertyChanged;
        _tabs.Add(tab);
        Raise(nameof(HasTabs));
        RecalculateAwaitingInput();
    }

    /// <summary>
    /// Убирает вкладку из полосы и, если она была активной, выбирает соседнюю.
    /// </summary>
    /// <returns>Вкладка, ставшая активной, либо <c>null</c>, если не осталось ни одной.</returns>
    public TabViewModel? Remove(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return ActiveTab;
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);

        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.IsActive = false;
        _tabs.RemoveAt(index);

        if (_lastActiveByProject.TryGetValue(tab.ProjectId, out var remembered)
            && ReferenceEquals(remembered, tab))
        {
            _lastActiveByProject.Remove(tab.ProjectId);
        }

        Raise(nameof(HasTabs));
        RecalculateAwaitingInput();

        if (!wasActive)
        {
            return ActiveTab;
        }

        if (_tabs.Count == 0)
        {
            SetActive(null);
            return null;
        }

        // Соседняя слева, если закрыли последнюю в ряду, иначе вставшая на это место.
        var next = _tabs[Math.Min(index, _tabs.Count - 1)];
        return next;
    }

    /// <summary>Делает вкладку активной; <c>null</c> снимает выделение со всех.</summary>
    public void SetActive(TabViewModel? tab)
    {
        foreach (var candidate in _tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        if (tab is not null)
        {
            _lastActiveByProject[tab.ProjectId] = tab;
        }

        ActiveTab = tab;
    }

    /// <summary>Вкладка по идентификатору терминала; <c>null</c>, если такой нет.</summary>
    public TabViewModel? Find(TerminalId terminalId) =>
        _tabs.FirstOrDefault(tab => tab.TerminalId == terminalId);

    /// <summary>Сколько вкладок открыто в проекте.</summary>
    public int CountFor(Guid projectId) => _tabs.Count(tab => tab.ProjectId == projectId);

    /// <summary>
    /// Активная вкладка проекта: последняя, на которой пользователь был, иначе первая открытая.
    /// <c>null</c>, если у проекта нет вкладок.
    /// </summary>
    public TabViewModel? ActiveTabFor(Guid projectId)
    {
        if (_lastActiveByProject.TryGetValue(projectId, out var remembered) && _tabs.Contains(remembered))
        {
            return remembered;
        }

        return _tabs.FirstOrDefault(tab => tab.ProjectId == projectId);
    }

    /// <summary>Вкладка по номеру 1..9; <c>null</c>, если столько вкладок не открыто.</summary>
    public TabViewModel? ByNumber(int number) =>
        number >= 1 && number <= _tabs.Count ? _tabs[number - 1] : null;

    /// <summary>Следующая вкладка по кругу; <c>null</c>, если вкладок нет.</summary>
    public TabViewModel? Next() => Shift(1);

    /// <summary>Предыдущая вкладка по кругу; <c>null</c>, если вкладок нет.</summary>
    public TabViewModel? Previous() => Shift(-1);

    private TabViewModel? Shift(int delta)
    {
        if (_tabs.Count == 0)
        {
            return null;
        }

        var current = ActiveTab is null ? -1 : _tabs.IndexOf(ActiveTab);
        if (current < 0)
        {
            return _tabs[0];
        }

        var index = (current + delta + _tabs.Count) % _tabs.Count;
        return _tabs[index];
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.IsAwaitingInput))
        {
            RecalculateAwaitingInput();
        }
    }

    private void RecalculateAwaitingInput() =>
        AwaitingInputCount = _tabs.Count(tab => tab.IsAwaitingInput);
}
