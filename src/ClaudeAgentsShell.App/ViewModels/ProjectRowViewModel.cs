using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Строка панели проектов. Знает о проекте ровно то, что видно на экране, и ничего
/// не делает сама: запуск сессий и переключение вкладок живут в <see cref="ShellViewModel"/>.
/// </summary>
public sealed class ProjectRowViewModel : ObservableObject
{
    private string? _branch;
    private bool _isAvailable = true;
    private int _sessionCount;
    private bool _isCurrent;

    /// <inheritdoc cref="ProjectRowViewModel" />
    public ProjectRowViewModel(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
    }

    /// <summary>Проект, который показывает строка.</summary>
    public ProjectDefinition Project { get; private set; }

    /// <summary>
    /// Заменяет описание проекта: диалог настроек вернул отредактированное либо сместился
    /// порядок после удаления соседней строки. Идентификатор строки при этом не меняется,
    /// поэтому вкладки, открытые в этом проекте, остаются привязанными к ней.
    /// </summary>
    public void Update(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (Project == project)
        {
            return;
        }

        Project = project;
        Raise(nameof(Project));
        Raise(nameof(Name));
        Raise(nameof(Path));
        Raise(nameof(PathAndBranch));
    }

    /// <summary>Идентификатор проекта.</summary>
    public Guid Id => Project.Id;

    /// <summary>Отображаемое имя.</summary>
    public string Name => Project.Name;

    /// <summary>Рабочий каталог.</summary>
    public string Path => Project.Path;

    /// <summary>Текущая ветка git; <c>null</c>, если каталог не репозиторий.</summary>
    public string? Branch
    {
        get => _branch;
        set
        {
            if (SetProperty(ref _branch, value))
            {
                Raise(nameof(PathAndBranch));
            }
        }
    }

    /// <summary>Вторая строка: путь и ветка моноширинным шрифтом.</summary>
    public string PathAndBranch => string.IsNullOrEmpty(Branch) ? Path : Path + " · " + Branch;

    /// <summary>
    /// Каталог на месте. Исчез (раздел 8 ТЗ) — строка помечена недоступной, запуск заблокирован.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    /// <summary>Число открытых вкладок этого проекта. Считается по списку вкладок, не накоплением.</summary>
    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (SetProperty(ref _sessionCount, value))
            {
                Raise(nameof(HasSessions));
                Raise(nameof(SessionCountText));
            }
        }
    }

    /// <summary>У проекта есть открытые вкладки.</summary>
    public bool HasSessions => SessionCount > 0;

    /// <summary>То, что показано справа в покое: число открытых сессий либо тире.</summary>
    public string SessionCountText => HasSessions
        ? SessionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    /// <summary>Проект активной вкладки — строка подсвечена.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
