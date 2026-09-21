using System.Diagnostics;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Двухфазное закрытие главного окна: решение «спрятать / отменить / закрыть» и общий
/// потолок на гашение.
/// <para>
/// Честное гашение псевдоконсолей занимает секунды, а отменять закрытие приходится на
/// каждой попытке, пока оно не закончено, — иначе повторный клик по крестику закрыл бы
/// окно посреди цепочки и псевдоконсоли пережили бы процесс. Чтобы это ожидание не
/// выглядело зависшим крестиком, первая попытка прячет окно: с экрана и с панели задач
/// приложение исчезает сразу, а процесс доживает оставшиеся секунды невидимым.
/// </para>
/// <para>
/// Логика вынесена из окна, потому что окно целиком тестом не покрывается, а пара флагов
/// и потолок — покрываются. В <see cref="MainWindow"/> остаётся только проводка.
/// </para>
/// </summary>
internal sealed class WindowShutdownSequence
{
    /// <summary>
    /// Общий потолок на всё гашение. Вышли за него — ждать перестаём и закрываемся.
    /// <para>
    /// Вложенные бюджеты (ожидание цикла помпы, освобождение псевдоконсоли, выход оболочки)
    /// складываются в верхнюю границу, которая нигде не выражена одним числом и растёт с
    /// каждым новым шагом гашения. Это число — единственная граница, за которой невидимый
    /// процесс считается зависшим.
    /// </para>
    /// <para>
    /// Осознанно принятый худший случай: по истечении потолка брошенное гашение
    /// продолжается в фоне, окно закрывается, и <c>App.OnExit</c> освобождает контейнер,
    /// чьи объекты уже помечены освобождёнными и вернутся мгновенно. Недогашенные
    /// псевдоконсоли в этом случае гибнут вместе с процессом — невежливо, но всё же
    /// лучше, чем невидимый процесс, висящий вечно.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(8);

    private readonly Func<Task> _shutdown;
    private readonly Action _hide;
    private readonly Action _close;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _deadline;

    private bool _started;
    private bool _completed;

    /// <param name="shutdown">Собственно гашение: освобождение вкладок и моста.</param>
    /// <param name="hide">Убрать окно с экрана — вызывается один раз, на первой попытке.</param>
    /// <param name="close">Закрыть окно — вызывается один раз, когда гашение кончилось или вышло за потолок.</param>
    /// <param name="timeProvider">Источник времени для потолка.</param>
    internal WindowShutdownSequence(
        Func<Task> shutdown,
        Action hide,
        Action close,
        TimeProvider timeProvider)
        : this(shutdown, hide, close, timeProvider, ShutdownDeadline)
    {
    }

    /// <param name="shutdown">Собственно гашение: освобождение вкладок и моста.</param>
    /// <param name="hide">Убрать окно с экрана — вызывается один раз, на первой попытке.</param>
    /// <param name="close">Закрыть окно — вызывается один раз, когда гашение кончилось или вышло за потолок.</param>
    /// <param name="timeProvider">Источник времени для потолка.</param>
    /// <param name="deadline">Потолок, отличный от <see cref="ShutdownDeadline"/>. Нужен тестам.</param>
    internal WindowShutdownSequence(
        Func<Task> shutdown,
        Action hide,
        Action close,
        TimeProvider timeProvider,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _shutdown = shutdown;
        _hide = hide;
        _close = close;
        _timeProvider = timeProvider;
        _deadline = deadline;
    }

    /// <summary>
    /// Гашение, запущенное первой попыткой; <c>null</c>, пока попыток не было. Приложению
    /// ждать его незачем — окно закроется само; свойство открыто ради тестов, которым
    /// нужна точка синхронизации вместо сна.
    /// </summary>
    internal Task? Running { get; private set; }

    /// <summary>
    /// Обрабатывает попытку закрытия окна.
    /// <para>
    /// Первая попытка прячет окно и запускает гашение, любая следующая во время гашения не
    /// делает ничего. Побочные действия здесь намеренно: решение и есть «спрятать сейчас,
    /// закрыть потом», и разделять его с вызывающим значило бы снова размазать логику по окну.
    /// </para>
    /// </summary>
    /// <returns><c>true</c>, если закрытие надо отменить; <c>false</c>, когда гашение уже закончено и окно можно закрывать.</returns>
    internal bool HandleCloseRequest()
    {
        if (_completed)
        {
            return false;
        }

        if (!_started)
        {
            _started = true;

            // Окно уходит с экрана и с панели задач до того, как начнётся ожидание.
            // Оно остаётся открытым — спрятанное окно закрывается как обычное, и
            // ShutdownMode="OnMainWindowClose" сработает на его Close как всегда.
            _hide();

            Running = RunAsync();
        }

        return true;
    }

    private async Task RunAsync()
    {
        // Возврат в очередь диспетчера до первого шага. Гашение, которое почему-то
        // закончилось синхронно, иначе позвало бы Close() прямо изнутри OnClosing — WPF
        // на закрытие посреди закрытия отвечает исключением.
        await Task.Yield();

        var shutdown = _shutdown();

        try
        {
            // ConfigureAwait здесь не ставится намеренно: Hide и Close принадлежат потоку
            // интерфейса, и продолжение обязано вернуться туда же, откуда пришла попытка.
            await shutdown.WaitAsync(_deadline, _timeProvider);
        }
        catch (TimeoutException)
        {
            // Потолок исчерпан. WaitAsync не отменяет само гашение — оно продолжается в
            // фоне и может упасть уже после того, как окна не станет, поэтому его исход
            // наблюдается отдельно.
            Observe(shutdown);
        }
        finally
        {
            // Что бы ни случилось при освобождении — сбой, потолок, — окно должно
            // закрыться: невидимый процесс, висящий вечно, хуже недогашенной оболочки.
            // Снятый флаг пропускает следующий вход без отмены.
            _completed = true;
            _close();
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => Trace.TraceError(
                "Claude Agents Shell: гашение не уложилось в потолок и завершилось ошибкой: {0}",
                completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
