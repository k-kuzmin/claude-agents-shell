using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Пишет раскладку окна в <see cref="ILayoutStore" /> по изменениям (issue #4): серия
/// сигналов склеивается в одну запись через <see cref="DebounceDelay" /> тишины. Опроса нет —
/// сигналы дёргают уже существующие события вкладок.
/// <para>
/// Снимок раскладки берётся <b>в момент записи</b>, а не в момент сигнала: живость вкладки
/// (<c>SessionEnd</c> → <c>SessionStart</c> после <c>/clear</c>) к этому времени успевает
/// устояться, и дебаунс сглаживает промежуточное «вкладка мертва».
/// </para>
/// <para>
/// Три состояния, переходы только вперёд: «ещё не записываю» (до конца восстановления —
/// иначе частично поднятая раскладка затёрла бы сохранённую), «записываю» и «заморожен»
/// (закрытие окна: гашение псевдоконсолей шлёт <c>TerminalExited</c> и <c>SessionEnd</c>,
/// и без заморозки последней записью оказалась бы пустая раскладка). Заморозка окончательна:
/// <see cref="Start" /> после неё ничего не включает.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="Start" />, <see cref="Signal" />, <see cref="FlushAndFreezeAsync" /> и
/// <see cref="Freeze" /> вызываются в потоке интерфейса; снимок тоже снимается там —
/// таймер переводит срабатывание через <see cref="IUiDispatcher" />.
/// </remarks>
public sealed class LayoutRecorder : IDisposable
{
    /// <summary>Окно тишины, после которого изменения раскладки уходят на диск.</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Источник записи в журнале сбоев: раскладку не удалось записать.</summary>
    public const string CrashSource = "LayoutRecorder";

    private readonly ILayoutStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly ICrashLog _log;
    private readonly ITimer _timer;

    private Func<WorkspaceLayout>? _capture;
    private bool _frozen;
    private bool _dirty;
    private bool _disposed;
    private Task _writes = Task.CompletedTask;

    /// <param name="store">Хранилище раскладки.</param>
    /// <param name="timeProvider">Источник времени для дебаунса.</param>
    /// <param name="dispatcher">Поток интерфейса: снимок вкладок снимается там.</param>
    /// <param name="log">Журнал сбоев записи.</param>
    public LayoutRecorder(ILayoutStore store, TimeProvider timeProvider, IUiDispatcher dispatcher, ICrashLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _dispatcher = dispatcher;
        _log = log;
        _timer = timeProvider.CreateTimer(
            static state => ((LayoutRecorder)state!).OnElapsed(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Сигналы превращаются в записи: восстановление закончено и заморозки не было.</summary>
    public bool IsRecording => _capture is not null && !_frozen;

    /// <summary>
    /// Включает запись после восстановления раскладки и ставит первую запись: восстановленное
    /// может отличаться от сохранённого (пропущенные вкладки удалённых проектов).
    /// Повторный вызов и вызов после заморозки ничего не делают.
    /// </summary>
    /// <param name="capture">Снимок текущей раскладки; зовётся в потоке интерфейса.</param>
    public void Start(Func<WorkspaceLayout> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (_frozen || _disposed || _capture is not null)
        {
            return;
        }

        _capture = capture;
        Signal();
    }

    /// <summary>Раскладка изменилась: запись отодвигается на полное окно тишины.</summary>
    public void Signal()
    {
        if (!IsRecording || _disposed)
        {
            return;
        }

        _dirty = true;
        _timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Закрытие окна, <b>до</b> гашения псевдоконсолей: отложенная запись выполняется сразу
    /// по текущему снимку, после чего регистратор замораживается. Снимок снимается синхронно,
    /// внутри вызова, — события гашения, пришедшие после возврата, в него уже не попадут.
    /// Записывать нечего (изменений не было или запись ещё не включена) — диск не трогается.
    /// </summary>
    /// <returns>Задача, завершающаяся, когда все начатые записи дошли до диска.</returns>
    public Task FlushAndFreezeAsync(CancellationToken cancellationToken)
    {
        if (_frozen || _disposed)
        {
            return _writes;
        }

        var pending = IsRecording && _dirty;
        Freeze();

        if (pending)
        {
            Write(cancellationToken);
        }

        return _writes;
    }

    /// <summary>Замораживает без записи: страховка на пути освобождения контейнера.</summary>
    public void Freeze()
    {
        if (_frozen || _disposed)
        {
            return;
        }

        _frozen = true;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Freeze();
        _disposed = true;
        _timer.Dispose();
    }

    // Поток таймера: снимок вкладок принадлежит потоку интерфейса.
    private void OnElapsed() => _dispatcher.Post(WriteIfPending);

    private void WriteIfPending()
    {
        // Проверка повторяется здесь, а не только при взводе таймера: между срабатыванием
        // и выполнением в очереди интерфейса окно могли начать закрывать.
        if (!IsRecording || _disposed || !_dirty)
        {
            return;
        }

        Write(CancellationToken.None);
    }

    private void Write(CancellationToken cancellationToken)
    {
        _dirty = false;
        var snapshot = _capture!();

        // Записи идут цепочкой: иначе более старый снимок мог бы лечь на диск позже нового.
        _writes = SaveAfterAsync(_writes, snapshot, cancellationToken);
    }

    private async Task SaveAfterAsync(Task previous, WorkspaceLayout snapshot, CancellationToken cancellationToken)
    {
        // Предыдущая запись свои сбои гасит сама, поэтому здесь её ожидание не бросает.
        await previous.ConfigureAwait(false);

        try
        {
            await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Раскладка — удобство, а не данные пользователя: сбой записи уходит в журнал
            // и не валит поток. Снимок помечается неушедшим, чтобы закрытие окна повторило запись.
            try
            {
                _dispatcher.Post(() => _dirty = true);
                _log.Write(CrashSource, exception);
            }
            catch
            {
                // Журнал и диспетчер обязаны не бросать, но полагаться на это здесь нельзя:
                // сбой из этой ветки сорвал бы всю цепочку последующих записей.
            }
        }
    }
}
