using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Вкладка-сессия. О ConPTY и странице терминалов не знает ничего: только идентификатор,
/// заголовок и состояние.
/// </summary>
public sealed class TabViewModel : ObservableObject
{
    /// <summary>Короткое имя сессии до первого сообщения пользователя (раздел 6.3 ТЗ).</summary>
    public const string NewSessionTitle = "новая сессия";

    private string _shortTitle = NewSessionTitle;
    private TabState _state = TabState.Unknown;
    private bool _isActive;
    private bool _isRunning = true;

    /// <inheritdoc cref="TabViewModel" />
    /// <param name="terminalId">Идентификатор вкладки в протоколе моста.</param>
    /// <param name="projectId">Проект, в котором открыта сессия.</param>
    /// <param name="projectName">Имя проекта для заголовка.</param>
    public TabViewModel(TerminalId terminalId, Guid projectId, string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        TerminalId = terminalId;
        ProjectId = projectId;
        ProjectName = projectName;
    }

    /// <summary>Идентификатор терминала на странице.</summary>
    public TerminalId TerminalId { get; }

    /// <summary>Проект вкладки.</summary>
    public Guid ProjectId { get; }

    /// <summary>Имя проекта.</summary>
    public string ProjectName { get; }

    /// <summary>
    /// Короткое имя сессии. До первого сообщения пользователя — «новая сессия»;
    /// настоящие заголовки приходят в M4.
    /// </summary>
    public string ShortTitle
    {
        get => _shortTitle;
        set
        {
            if (SetProperty(ref _shortTitle, value))
            {
                Raise(nameof(Title));
            }
        }
    }

    /// <summary>Заголовок вкладки: <c>&lt;проект&gt; · &lt;короткое имя сессии&gt;</c>.</summary>
    public string Title => ProjectName + " · " + ShortTitle;

    /// <summary>
    /// Состояние вкладки. Источник — хуки Claude Code (M4); разбирать вывод агента запрещено,
    /// поэтому до M4 состояние остаётся <see cref="TabState.Unknown"/>.
    /// </summary>
    public TabState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                Raise(nameof(IsAwaitingInput));
            }
        }
    }

    /// <summary>Вкладка ждёт ввода — для счётчика в полосе вкладок.</summary>
    public bool IsAwaitingInput => State == TabState.AwaitingInput;

    /// <summary>Вкладка выбрана.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Процесс оболочки ещё жив. Снимается по событию <c>TerminalExited</c>; сама вкладка
    /// при этом остаётся открытой — закрывает её только пользователь.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }
}
