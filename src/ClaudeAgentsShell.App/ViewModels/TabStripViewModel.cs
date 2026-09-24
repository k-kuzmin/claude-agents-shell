using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudeAgentsShell.App.State;
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
public sealed class TabStripViewModel : ObservableObject, IAwaitingTabs
{
    // Все открытые вкладки в порядке полосы: сначала в порядке открытия, а после того как
    // пользователь перетащил вкладку мышью — в том порядке, в каком он их расставил.
    // Счётчики на строках проектов, поиск по идентификатору терминала и гашение работают
    // по этому списку, а не по видимой части.
    private readonly List<TabViewModel> _all = [];

    // Вкладки выбранного проекта — то, что видит пользователь и по чему ходят Ctrl+Tab
    // и Ctrl+1..9.
    private readonly ObservableCollection<TabViewModel> _visible = [];

    // Какая вкладка проекта была активной последней: возврат к проекту возвращает
    // пользователя именно туда, а не на первую попавшуюся вкладку.
    private readonly Dictionary<Guid, TabViewModel> _lastActiveByProject = [];

    // Вкладки, которые сейчас ждут ввода. Из него — и счётчик без прохода по всем вкладкам,
    // и грани «пришла/ушла» по каждой вкладке: сравнение счётчиков их не различает, когда
    // одна вкладка уходит из ожидания, а другая в него приходит.
    private readonly HashSet<TabViewModel> _awaiting = [];

    private Guid? _projectId;
    private TabViewModel? _activeTab;
    private int _awaitingInputCount;
    private int _stateRevision;

    /// <inheritdoc cref="TabStripViewModel" />
    public TabStripViewModel() => Tabs = new ReadOnlyObservableCollection<TabViewModel>(_visible);

    /// <summary>Вкладки выбранного проекта в порядке полосы — ровно то, что видит пользователь.</summary>
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
    /// Источник состояния — хуки; пока состояние не пришло, счётчик равен нулю и в разметке скрыт.
    /// Клик по счётчику (раздел 6.3 ТЗ) ведёт на <see cref="NextAwaitingInput"/>.
    /// </summary>
    public int AwaitingInputCount
    {
        get => _awaitingInputCount;
        private set
        {
            // HasAwaitingInput — булев, и поднимать его на каждое изменение счётчика значит
            // звать лишний реквери команд окна на переходах вида 1 → 2. Поднимаем на грани.
            bool had = _awaitingInputCount > 0;
            if (SetProperty(ref _awaitingInputCount, value) && had != value > 0)
            {
                Raise(nameof(HasAwaitingInput));
            }
        }
    }

    /// <summary>Счётчик показывается только когда есть кого считать.</summary>
    public bool HasAwaitingInput => AwaitingInputCount > 0;

    /// <inheritdoc />
    /// <remarks>Счётчик к моменту события уже пересчитан.</remarks>
    public event EventHandler<TabViewModel>? TabBecameAwaiting;

    /// <inheritdoc />
    /// <remarks>Счётчик к моменту события уже пересчитан.</remarks>
    public event EventHandler<TabViewModel>? TabLeftAwaiting;

    /// <summary>
    /// Растёт на единицу каждый раз, когда у любой открытой вкладки поменялось состояние.
    /// Само число ничего не значит и на экране не показывается: это способ сказать наружу
    /// «маркеры устарели», не подписывая слушателя на каждую вкладку по отдельности —
    /// учёт подписок на вкладки живёт здесь и больше нигде.
    /// <para>
    /// Нужен строкам проектов: их точка состояния считается по вкладкам
    /// (<see cref="MarkerStateFor" />), а состав вкладок при смене состояния не меняется,
    /// и обычных уведомлений полосы для перекраски не хватает.
    /// </para>
    /// </summary>
    public int StateRevision
    {
        get => _stateRevision;
        private set => SetProperty(ref _stateRevision, value);
    }

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

        // Вкладка могла прийти уже ждущей — например, состояние выставили до добавления.
        TrackAwaiting(tab);
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

        // Закрытая в ожидании вкладка обязана дать грань ухода: иначе её уведомление
        // пережило бы саму вкладку. Состояние у неё при этом не меняется.
        if (_awaiting.Remove(tab))
        {
            AwaitingInputCount = _awaiting.Count;
            TabLeftAwaiting?.Invoke(this, tab);
        }

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
    /// Состояние проекта одной точкой: самое важное среди состояний **всех** его вкладок,
    /// а не только показанных в полосе. У проекта без вкладок — <see cref="TabState.Unknown" />.
    /// <para>
    /// Приоритет сверху вниз: <see cref="TabState.AwaitingInput" />, <see cref="TabState.Busy" />,
    /// <see cref="TabState.BackgroundWork" />, <see cref="TabState.Idle" />,
    /// <see cref="TabState.Unknown" />. Смысл: «меня ждут» важнее «идёт работа», а любая
    /// известная работа важнее простоя.
    /// </para>
    /// </summary>
    public TabState MarkerStateFor(Guid projectId)
    {
        var best = TabState.Unknown;
        var bestRank = 0;

        foreach (var tab in _all)
        {
            if (tab.ProjectId != projectId)
            {
                continue;
            }

            var rank = MarkerRank(tab.State);
            if (rank > bestRank)
            {
                best = tab.State;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// Активная вкладка проекта: последняя, на которой пользователь был, иначе самая левая в полосе.
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

    /// <summary>
    /// Следующая после <paramref name="current" /> вкладка, ждущая ввода, среди **всех**
    /// проектов, по кругу; <c>null</c>, если таких нет. Цель клика по счётчику «N ждёт ввода»:
    /// повторные клики обходят все ждущие вкладки, а не упираются в одну и ту же.
    /// Если <paramref name="current" /> не задана или уже закрыта — самая первая ждущая.
    /// Единственная ждущая вкладка, она же текущая, возвращается сама.
    /// <para>
    /// Порядок — порядок полосы, а **не** порядок попадания в «ждёт ввода»: из двух
    /// ждущих раньше придёт стоящая левее, даже если ждать она начала позже. Порядок полосы — это
    /// порядок открытия до тех пор, пока пользователь не переставил вкладки мышью
    /// (см. <see cref="Reorder" />); после перестановки — выбранный им порядок. Видимая полоса
    /// строится фильтрацией того же списка с сохранением порядка, поэтому внутри проекта это
    /// всегда обход слева направо. Порядок не зависит от того, как пользователь
    /// переставил проекты в панели.
    /// </para>
    /// </summary>
    public TabViewModel? NextAwaitingInput(TabViewModel? current)
    {
        int start = current is null ? -1 : _all.IndexOf(current);
        for (int step = 1; step <= _all.Count; step++)
        {
            var tab = _all[(start + step + _all.Count) % _all.Count];
            if (tab.IsAwaitingInput)
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>
    /// Переставляет вкладку в полосе: снимает её с текущего места и вставляет в промежуток
    /// <paramref name="gapIndex" /> — перед вкладкой с этим номером в видимом порядке.
    /// Промежуток, равный числу видимых вкладок, означает конец полосы; значения вне диапазона
    /// подрезаются, поэтому бросок за край полосы ставит вкладку с краю, а не теряется.
    /// <para>
    /// Порядок в списке всех вкладок остаётся согласованным с видимым: вкладки выбранного
    /// проекта раскладываются по своим же местам в общем списке в новом порядке, а вкладки
    /// остальных проектов не сдвигаются вовсе. Поэтому видимая полоса и после перестановки
    /// получается фильтрацией <see cref="AllTabs" /> с сохранением порядка, а возврат к проекту
    /// показывает вкладки так, как их расставил пользователь.
    /// </para>
    /// </summary>
    /// <param name="tab">Перетаскиваемая вкладка; вкладка не из полосы игнорируется.</param>
    /// <param name="gapIndex">Промежуток полосы 0..<see cref="Tabs" />.Count, куда её вставить.</param>
    /// <returns><c>true</c>, если порядок действительно изменился.</returns>
    public bool Reorder(TabViewModel tab, int gapIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var from = _visible.IndexOf(tab);
        if (from < 0)
        {
            // Вкладка чужого проекта или уже закрытая: переставлять в полосе нечего.
            return false;
        }

        var gap = Math.Clamp(gapIndex, 0, _visible.Count);

        // Промежутки считаются по полосе вместе с самой перетаскиваемой вкладкой, а на новом
        // месте её там уже нет: всё, что правее, съезжает на одну позицию влево.
        var target = gap > from ? gap - 1 : gap;
        if (target == from)
        {
            return false;
        }

        _visible.Move(from, target);
        ReplayVisibleOrderIntoAll();
        return true;
    }

    /// <summary>Вкладка полосы по номеру 1..9; <c>null</c>, если столько вкладок не показано.</summary>
    public TabViewModel? ByNumber(int number) =>
        number >= 1 && number <= _visible.Count ? _visible[number - 1] : null;

    /// <summary>Следующая вкладка полосы по кругу; <c>null</c>, если полоса пуста.</summary>
    public TabViewModel? Next() => Shift(1);

    /// <summary>Предыдущая вкладка полосы по кругу; <c>null</c>, если полоса пуста.</summary>
    public TabViewModel? Previous() => Shift(-1);

    // Вес состояния для точки проекта. Порядок намеренно не совпадает с числовым порядком
    // TabState: BackgroundWork объявлен последним, но по важности стоит ниже Busy, а выше
    // всех — AwaitingInput. Поэтому сравнить состояния напрямую (Max) нельзя.
    private static int MarkerRank(TabState state) => state switch
    {
        TabState.AwaitingInput => 4,
        TabState.Busy => 3,
        TabState.BackgroundWork => 2,
        TabState.Idle => 1,
        _ => 0,
    };

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

    // Раскладывает вкладки выбранного проекта по их же местам в общем списке — в новом
    // видимом порядке. Места вкладок остальных проектов при этом не трогаются, поэтому
    // перестановка внутри одного проекта не двигает соседние.
    private void ReplayVisibleOrderIntoAll()
    {
        var next = 0;
        for (var i = 0; i < _all.Count; i++)
        {
            if (IsVisible(_all[i]))
            {
                _all[i] = _visible[next++];
            }
        }
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
        // Только State: его сеттер поднимает и IsAwaitingInput, поэтому реакция на оба
        // свойства давала бы две одинаковые реакции на одно логическое изменение.
        if (e.PropertyName is nameof(TabViewModel.State) && sender is TabViewModel tab)
        {
            TrackAwaiting(tab);

            // Состав вкладок не изменился, а точки на строках проектов устарели: считаются
            // они по состояниям вкладок. Уведомление наружу идёт отсюда, чтобы слушателю
            // не подписываться на каждую вкладку самому.
            StateRevision++;
        }
    }

    // Сводит состояние одной вкладки с множеством ждущих. Счётчик выставляется до события:
    // слушатель грани читает HasAwaitingInput и должен видеть уже новое значение.
    private void TrackAwaiting(TabViewModel tab)
    {
        if (tab.IsAwaitingInput)
        {
            if (_awaiting.Add(tab))
            {
                AwaitingInputCount = _awaiting.Count;
                TabBecameAwaiting?.Invoke(this, tab);
            }
        }
        else if (_awaiting.Remove(tab))
        {
            AwaitingInputCount = _awaiting.Count;
            TabLeftAwaiting?.Invoke(this, tab);
        }
    }
}
