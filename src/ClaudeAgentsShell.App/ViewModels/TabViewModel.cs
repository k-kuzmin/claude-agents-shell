using System.Globalization;
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

    private string _projectName;
    private string _shortTitle = NewSessionTitle;
    private TabState _state = TabState.Unknown;
    private bool _isActive;
    private bool _isRunning = true;
    private int? _exitCode;

    /// <inheritdoc cref="TabViewModel" />
    /// <param name="terminalId">Идентификатор вкладки в протоколе моста.</param>
    /// <param name="projectId">Проект, в котором открыта сессия.</param>
    /// <param name="projectName">Имя проекта для заголовка.</param>
    /// <param name="workingDirectory">Каталог, в котором запущена сессия вкладки.</param>
    public TabViewModel(TerminalId terminalId, Guid projectId, string projectName, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        TerminalId = terminalId;
        ProjectId = projectId;
        _projectName = projectName;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Идентификатор терминала на странице.</summary>
    public TerminalId TerminalId { get; }

    /// <summary>Проект вкладки.</summary>
    public Guid ProjectId { get; }

    /// <summary>
    /// Имя проекта в заголовке вкладки. Меняется вместе с именем проекта: имя — вещь
    /// отображаемая, и после переименования на экране не должно оказаться двух имён
    /// одного проекта — нового в панели и старого на вкладках (раздел 6.3 ТЗ).
    /// Пустое имя игнорируется: заголовок без проекта — сломанный контракт.
    /// </summary>
    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (SetProperty(ref _projectName, value))
            {
                Raise(nameof(Title));
            }
        }
    }

    /// <summary>
    /// Каталог, в котором запущена сессия вкладки. Снимается при открытии и дальше
    /// <b>не меняется</b> — в том числе при переносе проекта в другой каталог: псевдоконсоль
    /// работает там, где её запустили, и транскрипт сессии лежит в slug'е именно этого
    /// каталога. Отдать вместо него новый путь проекта значило бы искать транскрипт не там.
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// Короткое имя сессии. До первого сообщения пользователя — «новая сессия»; дальше его
    /// выставляет координатор состояний, вычитав первое сообщение из транскрипта.
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
    /// Состояние вкладки. Единственный источник — хуки Claude Code; разбирать вывод
    /// агента запрещено. Хуки не подключились — состояние остаётся <see cref="TabState.Unknown"/>,
    /// и это допустимая деградация, а не ошибка.
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
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Процесс оболочки завершился. Обратная сторона <see cref="IsRunning"/>: разметке нужен
    /// именно такой знак, потому что показывать пометку и кнопку «перезапустить» надо на
    /// мёртвой вкладке, а обратного преобразователя булева значения в проекте нет.
    /// </summary>
    public bool HasExited => !_isRunning;

    /// <summary>
    /// Код выхода оболочки; <c>null</c>, пока процесс жив. Источник — событие
    /// <c>TerminalExited</c>, а не разбор вывода вкладки.
    /// </summary>
    public int? ExitCode => _exitCode;

    /// <summary>
    /// Выход был ненулевым, то есть процесс упал. Штатный выход из оболочки (код 0) — это
    /// не падение, и пометка о нём красится спокойным цветом (раздел 8 ТЗ).
    /// </summary>
    public bool HasFailedExit => _exitCode is not null and not 0;

    /// <summary>
    /// Пометка о коде выхода на вкладке. Пустая, пока процесс жив: видеть код надо
    /// не наводя мышь, поэтому это текст, а не всплывающая подсказка.
    /// </summary>
    public string ExitBadgeText => _exitCode is { } code
        ? "код " + code.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>
    /// Отмечает, что процесс вкладки завершился с указанным кодом. Вкладка остаётся
    /// открытой — закрывает её только пользователь (раздел 8 ТЗ).
    /// </summary>
    /// <param name="exitCode">Код выхода оболочки.</param>
    /// <remarks>
    /// Один метод вместо сеттера <see cref="IsRunning"/>: флаг и код обязаны меняться вместе,
    /// иначе разметка успела бы показать пометку без кода. Состояние вкладки
    /// (<see cref="State"/>) здесь не трогается — его единственный источник координатор
    /// состояний, он же снимает маркер умершей вкладки.
    /// </remarks>
    public void MarkExited(int exitCode)
    {
        if (!_isRunning && _exitCode == exitCode)
        {
            return;
        }

        _isRunning = false;
        _exitCode = exitCode;

        Raise(nameof(IsRunning));
        Raise(nameof(HasExited));
        Raise(nameof(ExitCode));
        Raise(nameof(HasFailedExit));
        Raise(nameof(ExitBadgeText));
    }
}
