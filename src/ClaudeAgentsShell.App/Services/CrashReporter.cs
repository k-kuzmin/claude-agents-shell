using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Что делать с необработанным исключением: записать в журнал и, если это уместно,
/// показать пользователю окно.
/// <para>
/// Вынесен из <c>App.xaml.cs</c> отдельным классом, потому что решение «показывать или
/// нет» — единственная здесь логика, а обработчики событий приложения тестом не
/// покрываются. В <c>App.xaml.cs</c> остаётся тонкая проводка.
/// </para>
/// </summary>
internal sealed class CrashReporter
{
    /// <summary>Источник: исключение в потоке интерфейса, пойманное диспетчером.</summary>
    public const string DispatcherSource = "DispatcherUnhandledException";

    /// <summary>Источник: исключение, дошедшее до домена приложения. Процесс обычно уже не жилец.</summary>
    public const string AppDomainSource = "AppDomain.UnhandledException";

    /// <summary>Источник: сбой задачи, результат которой никто не посмотрел.</summary>
    public const string TaskSchedulerSource = "TaskScheduler.UnobservedTaskException";

    /// <summary>Сколько окон об ошибке показывается за одно окно времени.</summary>
    public const int MaxDialogsPerWindow = 3;

    /// <summary>Длина окна, после которой счётчик показов обнуляется.</summary>
    public static readonly TimeSpan DialogWindow = TimeSpan.FromMinutes(1);

    private const string DialogTitle = "Непредвиденная ошибка";

    private readonly ICrashLog _log;
    private readonly IUserPrompt _prompt;
    private readonly TimeProvider _time;

    // Сбой приходит с любого потока, поэтому предохранитель под замком. Замок держится
    // только на время решения: показ окна модальный и под замком запер бы остальных.
    private readonly object _gate = new();
    private bool _dialogOpen;
    private int _shownInWindow;
    private DateTimeOffset _windowStartedAt;

    /// <inheritdoc cref="CrashReporter" />
    public CrashReporter(ICrashLog log, IUserPrompt prompt, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(time);
        _log = log;
        _prompt = prompt;
        _time = time;
    }

    /// <summary>
    /// Только запись в журнал, без окна. Для случаев, когда показывать некому: домен уже
    /// заканчивает процесс, а необслуженная задача пользователю ни о чём не говорит.
    /// Не бросает.
    /// </summary>
    public void Record(string source, Exception exception) => SafeWrite(source, exception);

    /// <summary>
    /// Запись в журнал и, если предохранитель позволяет, окно с текстом ошибки.
    /// Не бросает: исключение отсюда убило бы процесс без следа.
    /// </summary>
    /// <returns><c>true</c>, если окно было показано.</returns>
    public bool Report(string source, Exception exception)
    {
        var path = SafeWrite(source, exception);

        if (!TryEnterDialog(out var last))
        {
            return false;
        }

        try
        {
            _prompt.ShowError(DialogTitle, BuildMessage(exception, path, last));
            return true;
        }
        catch
        {
            // Окно могло не открыться: главного окна ещё нет, интерфейс уже сносится,
            // менеджер окон отказал. Записать сбой мы успели, а падать в обработчике
            // падения нельзя — там его не поймает уже никто.
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _dialogOpen = false;
            }
        }
    }

    private static string BuildMessage(Exception? exception, string? logPath, bool last)
    {
        var lines = new List<string>(6)
        {
            "Произошла непредвиденная ошибка. Приложение продолжает работу, но состояние" +
            " могло испортиться — при странном поведении лучше перезапустить.",
            string.Empty,
            exception is null
                ? "Подробности неизвестны."
                : $"{exception.GetType().Name}: {exception.Message}",
        };

        if (logPath is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"Стек записан в {logPath}");
        }

        if (last)
        {
            lines.Add(string.Empty);
            lines.Add("Следующие ошибки в ближайшую минуту попадут только в журнал, без окна.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string? SafeWrite(string source, Exception exception)
    {
        try
        {
            return _log.Write(source, exception);
        }
        catch
        {
            // Порт обязан не бросать, но обработчик падения не то место, где стоит
            // полагаться на чужую дисциплину: чужая ошибка здесь стоит всего процесса.
            return null;
        }
    }

    /// <summary>
    /// Предохранитель от лавины. Сбой в отрисовке или в привязке повторяется на каждом
    /// кадре: без порога окна пошли бы очередью, которую нечем закрыть. Пока окно открыто,
    /// второго не будет (модальный показ крутит цикл сообщений, и повторный сбой приходит
    /// в тот же поток), а за одно окно времени их не больше <see cref="MaxDialogsPerWindow" />.
    /// </summary>
    /// <param name="last"><c>true</c>, если это последнее окно в текущем окне времени.</param>
    private bool TryEnterDialog(out bool last)
    {
        last = false;

        lock (_gate)
        {
            if (_dialogOpen)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            if (_shownInWindow == 0 || now - _windowStartedAt >= DialogWindow)
            {
                _windowStartedAt = now;
                _shownInWindow = 0;
            }

            if (_shownInWindow >= MaxDialogsPerWindow)
            {
                return false;
            }

            _shownInWindow++;
            _dialogOpen = true;
            last = _shownInWindow == MaxDialogsPerWindow;
            return true;
        }
    }
}
