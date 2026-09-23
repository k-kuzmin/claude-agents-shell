using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;

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
    /// Это страховка от зависшего гашения, а не сумма вложенных бюджетов: точной верхней
    /// границы у гашения нет. В <c>TerminalPump.DisposeAsync</c> есть ожидание замка
    /// сброса вообще без предела по времени — пока оно там, никакое число не будет
    /// гарантированной суммой.
    /// </para>
    /// <para>
    /// Нижнюю планку задаёт честный, просто медленный случай: одна помпа тратит до 3 с на
    /// ожидание цикла, до 2 с на повторное ожидание и до 5 с на освобождение
    /// псевдоконсоли. Эти десять секунд достижимы и на одной вкладке — помпы гасятся
    /// параллельно, так что на число вкладок они не множатся. Потолок обязан быть заметно
    /// выше: иначе он рвал бы не зависшее, а просто медленное гашение, и невежливо
    /// прибитая оболочка, записанная ниже как исключительный случай, наступала бы штатно.
    /// Удлинения пользователь не почувствует: окно к этому моменту уже спрятано, и платит
    /// он только за то, чтобы оболочки умирали по-честному.
    /// </para>
    /// <para>
    /// Осознанно принятый худший случай: по истечении потолка брошенное гашение
    /// продолжается в фоне, окно закрывается, и <c>App.OnExit</c> освобождает контейнер.
    /// Набор вкладок к этому моменту уже помечен освобождённым и вернётся мгновенно, а мост,
    /// до которого брошенное гашение могло не дойти, <see cref="ContainerTeardown"/>
    /// освобождает первым и в потоке интерфейса — иначе выход мог повиснуть. Недогашенные
    /// псевдоконсоли в этом случае гибнут вместе с процессом — невежливо, но всё же
    /// лучше, чем невидимый процесс, висящий вечно.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Источник записи в журнале: гашение упало, не дойдя до потолка.</summary>
    internal const string CrashSource = "WindowShutdownSequence";

    /// <summary>Источник записи в журнале: брошенное по потолку гашение упало уже в фоне.</summary>
    internal const string LateCrashSource = "WindowShutdownSequence.AfterDeadline";

    /// <summary>Источник записи в журнале: раскладку окна перед гашением записать не удалось.</summary>
    internal const string LayoutCrashSource = "WindowShutdownSequence.Layout";

    private readonly Func<Task> _persistLayout;
    private readonly Func<Task> _shutdown;
    private readonly Action _hide;
    private readonly Action _close;
    private readonly TimeProvider _timeProvider;
    private readonly ShutdownSignal _signal;
    private readonly ICrashLog _log;
    private readonly TimeSpan _deadline;

    private bool _started;
    private bool _completed;

    /// <param name="persistLayout">
    /// Запись раскладки окна и её заморозка (issue #4). Зовётся на первой попытке закрытия
    /// <b>до</b> гашения: гашение шлёт выход оболочек, и незамороженная раскладка записала бы
    /// пустой набор вкладок. Синхронная часть делегата выполняется прямо внутри попытки.
    /// </param>
    /// <param name="shutdown">Собственно гашение: освобождение вкладок и моста.</param>
    /// <param name="hide">Убрать окно с экрана — вызывается один раз, на первой попытке.</param>
    /// <param name="close">Закрыть окно — вызывается один раз, когда гашение кончилось или вышло за потолок.</param>
    /// <param name="timeProvider">Источник времени для потолка.</param>
    /// <param name="signal">Общий признак «гашение началось».</param>
    /// <param name="log">Журнал сбоев: туда уходит упавшее гашение — ждать его больше некому.</param>
    internal WindowShutdownSequence(
        Func<Task> persistLayout,
        Func<Task> shutdown,
        Action hide,
        Action close,
        TimeProvider timeProvider,
        ShutdownSignal signal,
        ICrashLog log)
        : this(persistLayout, shutdown, hide, close, timeProvider, signal, log, ShutdownDeadline)
    {
    }

    /// <param name="persistLayout">
    /// Запись раскладки окна и её заморозка (issue #4). Зовётся на первой попытке закрытия
    /// <b>до</b> гашения: гашение шлёт выход оболочек, и незамороженная раскладка записала бы
    /// пустой набор вкладок. Синхронная часть делегата выполняется прямо внутри попытки.
    /// </param>
    /// <param name="shutdown">Собственно гашение: освобождение вкладок и моста.</param>
    /// <param name="hide">Убрать окно с экрана — вызывается один раз, на первой попытке.</param>
    /// <param name="close">Закрыть окно — вызывается один раз, когда гашение кончилось или вышло за потолок.</param>
    /// <param name="timeProvider">Источник времени для потолка.</param>
    /// <param name="signal">Общий признак «гашение началось».</param>
    /// <param name="log">Журнал сбоев: туда уходит упавшее гашение — ждать его больше некому.</param>
    /// <param name="deadline">Потолок, отличный от <see cref="ShutdownDeadline"/>. Нужен тестам.</param>
    internal WindowShutdownSequence(
        Func<Task> persistLayout,
        Func<Task> shutdown,
        Action hide,
        Action close,
        TimeProvider timeProvider,
        ShutdownSignal signal,
        ICrashLog log,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(persistLayout);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(log);

        _persistLayout = persistLayout;
        _shutdown = shutdown;
        _hide = hide;
        _close = close;
        _timeProvider = timeProvider;
        _signal = signal;
        _log = log;
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

            // Признак взводится до Hide: с этого момента показывать модальные окна об
            // ошибке некому — включая сбой в самом Hide.
            _signal.MarkStarted();

            // Раскладка снимается и замораживается раньше всего остального: до первого шага
            // гашения и до того, как очередь интерфейса успеет выполнить хоть одно событие.
            var persisting = StartPersistingLayout();

            // Окно уходит с экрана и с панели задач до того, как начнётся ожидание.
            // Оно остаётся открытым — спрятанное окно закрывается как обычное, и
            // ShutdownMode="OnMainWindowClose" сработает на его Close как всегда.
            _hide();

            Running = RunAsync(persisting);
        }

        return true;
    }

    private async Task RunAsync(Task persisting)
    {
        // Возврат в очередь диспетчера до первого шага. Гашение, которое почему-то
        // закончилось синхронно, иначе позвало бы Close() прямо изнутри OnClosing — WPF
        // на закрытие посреди закрытия отвечает исключением.
        await Task.Yield();

        try
        {
            // Запуск гашения внутри try: синхронный бросок из делегата иначе унёс бы
            // управление мимо finally и оставил бы окно спрятанным навсегда.
            var shutdown = PersistThenShutdownAsync(persisting);

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
        }
        catch (Exception exception)
        {
            // Сбой гашения, уложившегося в потолок. Выпускать его наружу некуда: Running —
            // поле живого объекта, ждать которое некому, финализатора у держателя нет, и
            // UnobservedTaskException не сработает. Без этой ветки сбой до потолка не
            // попадал бы никуда, тогда как сбой после потолка журнал получает.
            Record(CrashSource, exception);
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

    private Task StartPersistingLayout()
    {
        try
        {
            return _persistLayout();
        }
        catch (Exception exception)
        {
            // Сбой раскладки не повод оставить окно висеть: гашение идёт дальше.
            Record(LayoutCrashSource, exception);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Сначала дожидается записи раскладки, затем гасит. Оба шага — под общим потолком.
    /// ConfigureAwait не ставится: гашение освобождает объекты потока интерфейса.
    /// </summary>
    private async Task PersistThenShutdownAsync(Task persisting)
    {
        try
        {
            await persisting;
        }
        catch (Exception exception)
        {
            Record(LayoutCrashSource, exception);
        }

        await _shutdown();
    }

    private void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => Record(LateCrashSource, completed.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Запись сбоя гашения в журнал. Оба исхода — сбой до потолка и сбой брошенного
    /// гашения после него — идут сюда, чтобы у одного класса не было двух разных
    /// представлений о том, куда девать собственную ошибку.
    /// </summary>
    private void Record(string source, Exception exception)
    {
        try
        {
            _log.Write(source, exception);
        }
        catch
        {
            // Порт обязан не бросать, но полагаться здесь на чужую дисциплину нельзя:
            // исключение отсюда снова оставило бы задачу гашения в Faulted — ровно то
            // состояние, от которого запись и заводилась.
        }
    }
}
