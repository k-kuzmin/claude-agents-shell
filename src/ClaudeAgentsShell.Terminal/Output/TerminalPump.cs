using System.Buffers;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Output;

/// <summary>
/// Владеет парой «псевдоконсоль ↔ вкладка»: читает вывод, копит его в пачки и отдаёт мосту,
/// проводит ввод и ресайз обратно в PTY. Реализует разделы 3.3–3.4 ТЗ.
/// <para>
/// Пачка уходит на страницу не чаще одного кадра (<see cref="TerminalOptions.FlushInterval"/>)
/// либо немедленно по достижении <see cref="TerminalOptions.FlushThresholdBytes"/>.
/// Таймер взводится только когда в буфере появились байты, и снимается сразу после сброса —
/// вхолостую он не тикает.
/// </para>
/// <para>
/// Backpressure: <c>WriteOutputAsync</c> завершается, когда страница подтвердила
/// <c>term.write</c>. Пока неподтверждённых пачек <see cref="TerminalOptions.MaxPendingWrites"/>
/// и больше, чтение из PTY не возобновляется.
/// </para>
/// </summary>
public sealed class TerminalPump : IAsyncDisposable
{
    private readonly TerminalId _terminalId;
    private readonly IPtySession _pty;
    private readonly ITerminalBridge _bridge;
    private readonly TerminalOptions _options;
    private readonly OutputAccumulator _accumulator;
    private readonly Queue<PendingWrite> _pending = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<int> _exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer _flushTimer;
    private readonly byte[] _readBuffer;
    private readonly object _sync = new();

    private Task? _loop;
    private bool _timerArmed;
    private bool _buffersReleased;
    private int _exitNotified;
    private bool _shuttingDown;
    private int _disposed;

    /// <param name="terminalId">Вкладка, которой принадлежит эта псевдоконсоль.</param>
    /// <param name="pty">Уже поднятая псевдоконсоль. Помпа становится её владельцем.</param>
    /// <param name="bridge">Мост на страницу терминалов.</param>
    /// <param name="options">Параметры горячего пути вывода.</param>
    /// <param name="timeProvider">Источник времени для кадра склейки; в тестах — управляемый.</param>
    public TerminalPump(
        TerminalId terminalId,
        IPtySession pty,
        ITerminalBridge bridge,
        TerminalOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(pty);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _terminalId = terminalId;
        _pty = pty;
        _bridge = bridge;
        _options = options;
        _accumulator = new OutputAccumulator(options.FlushThresholdBytes + options.ReadBufferBytes);
        _readBuffer = ArrayPool<byte>.Shared.Rent(options.ReadBufferBytes);

        _flushTimer = timeProvider.CreateTimer(
            static state => ((TerminalPump)state!).OnFlushTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _pty.Exited += OnPtyExited;
    }

    /// <summary>Запускает читающий цикл. Вызывается после того, как страница прислала <c>ready</c>.</summary>
    public void Start()
    {
        lock (_sync)
        {
            _loop ??= Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>Отдаёт пользовательский ввод в stdin псевдоконсоли.</summary>
    public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _pty.WriteAsync(data, cancellationToken);

    /// <summary>Применяет размер, пришедший со страницы. Мусорные значения отбрасываются здесь.</summary>
    public void Resize(TerminalSize size)
    {
        if (size.IsValid)
        {
            _pty.Resize(size);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Подписка на завершение процесса снимается в самом конце: закрытие псевдоконсоли
        // гасит оболочку, и её код выхода нужен читающему циклу, который в это время ещё
        // добирает хвост вывода. Отписка здесь потеряла бы сигнал и заставила бы цикл
        // ждать его до истечения таймаута.

        // Снимаем backpressure до закрытия псевдоконсоли: иначе цикл может стоять в ожидании
        // подтверждения от страницы, перестать вычитывать пайп — и ClosePseudoConsole зависнет.
        lock (_sync)
        {
            _shuttingDown = true;
        }

        _shutdownSignal.TrySetResult();

        var ptyDispose = _pty.DisposeAsync().AsTask();

        bool loopFinished = await WaitForLoopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        if (!loopFinished)
        {
            // Цикл не уложился в бюджет. Чаще всего он стоит на финальном сбросе, ожидая
            // квитанцию, которой уже не будет. Сообщение о завершении снимает учёт записей
            // вкладки в мосте — ожидание отпускается, и цикл получает шанс выйти по-человечески.
            await NotifyExitedAsync().ConfigureAwait(false);
            loopFinished = await WaitForLoopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        try
        {
            await ptyDispose.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Псевдоконсоль не закрылась вовремя; ресурсы процесса освободит ОС.
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        // Таймер снимаем до освобождения буферов: новых сбросов быть не должно.
        _flushTimer.Dispose();

        // Ждём сброс, который мог начаться до снятия таймера.
        await _flushLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _buffersReleased = loopFinished;

                while (_pending.Count > 0)
                {
                    Release(_pending.Dequeue());
                }

                if (!loopFinished)
                {
                    // Читающий цикл не вышел за отведённое время и может ещё писать в
                    // _readBuffer и в аккумулятор. Возврат их в пул отдал бы чужой вкладке
                    // используемую память — лучше потерять пару буферов: невозвращённый
                    // массив станет обычным мусором GC. По той же причине здесь не
                    // освобождается _cts — живой цикл ещё держит его токен.
                    return;
                }

                _accumulator.Dispose();
            }

            ArrayPool<byte>.Shared.Return(_readBuffer);
        }
        finally
        {
            _flushLock.Release();
        }

        _pty.Exited -= OnPtyExited;

        // Сюда попадаем только когда читающий цикл завершился и токен больше никому не нужен.
        // _flushLock, как и _writeLock в ConPtySession, не освобождается намеренно:
        // SemaphoreSlim без AvailableWaitHandle этого не требует, а его освобождение
        // создавало бы гонку со сбросом, который мог быть запущен таймером.
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ApplyBackpressureAsync(cancellationToken).ConfigureAwait(false);

                int read = await _pty.ReadAsync(_readBuffer.AsMemory(0, _options.ReadBufferBytes), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                bool flushNow;
                lock (_sync)
                {
                    _accumulator.Append(_readBuffer.AsSpan(0, read));
                    flushNow = _accumulator.Count >= _options.FlushThresholdBytes;
                }

                if (flushNow)
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ArmFlushTimer();
                }
            }

            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await NotifyExitedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Обычное завершение при закрытии вкладки.
        }
    }

    /// <summary>
    /// Держит число неподтверждённых пачек ниже порога. По ходу дела возвращает в пул буферы
    /// тех пачек, которые страница уже подтвердила.
    /// </summary>
    private async Task ApplyBackpressureAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingWrite blocking;

            lock (_sync)
            {
                while (_pending.Count > 0 && _pending.Peek().Task.IsCompleted)
                {
                    Release(_pending.Dequeue());
                }

                if (_shuttingDown || _pending.Count < _options.MaxPendingWrites)
                {
                    return;
                }

                blocking = _pending.Dequeue();
            }

            try
            {
                // Остановка вкладки освобождает ожидание, не прерывая чтения: пайп должен
                // вычитываться, пока ClosePseudoConsole дописывает хвост вывода.
                await Task.WhenAny(blocking.Task, _shutdownSignal.Task)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Release(blocking);
                throw;
            }

            // Task.WhenAny не пробрасывает исключение вложенной задачи — сбойную запись
            // (страница закрылась, упал рендерер) наблюдаем вручную, чтобы она не осталась
            // unobserved, и продолжаем читать.
            Release(blocking);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArraySegment<byte> batch;
            lock (_sync)
            {
                DisarmFlushTimer();

                if (_buffersReleased || _accumulator.Count == 0)
                {
                    return;
                }

                batch = _accumulator.Detach();
            }

            var write = _bridge
                .WriteOutputAsync(_terminalId, batch.AsMemory(), cancellationToken)
                .AsTask();

            lock (_sync)
            {
                _pending.Enqueue(new PendingWrite(write, batch));
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private void OnFlushTimer()
    {
        _ = FlushFromTimerAsync();
    }

    private async Task FlushFromTimerAsync()
    {
        try
        {
            await FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Вкладка закрывается.
        }
        catch (ObjectDisposedException)
        {
            // Помпа освобождена между взводом таймера и его срабатыванием.
        }
    }

    private void ArmFlushTimer()
    {
        lock (_sync)
        {
            if (_timerArmed || _shuttingDown)
            {
                return;
            }

            _timerArmed = true;
            _flushTimer.Change(_options.FlushInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmFlushTimer()
    {
        if (!_timerArmed)
        {
            return;
        }

        _timerArmed = false;
        _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task<bool> WaitForLoopAsync(TimeSpan budget)
    {
        if (_loop is not { } loop)
        {
            return true;
        }

        try
        {
            await loop.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // Читающий цикл ещё работает — его буферы трогать нельзя.
            return false;
        }
        catch (Exception)
        {
            // Цикл завершился с ошибкой: он уже не работает, буферы освобождать можно.
            return true;
        }
    }

    /// <summary>
    /// Сообщает странице код выхода ровно один раз. Кроме уведомления это снимает учёт
    /// записей вкладки в мосте, поэтому вызов обязателен и на пути, где читающий цикл
    /// не дошёл до конца сам.
    /// </summary>
    private async Task NotifyExitedAsync()
    {
        if (Interlocked.Exchange(ref _exitNotified, 1) == 1)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = await _exitSignal.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            exitCode = _pty.ExitCode ?? -1;
        }

        try
        {
            await _bridge.NotifyExitedAsync(_terminalId, exitCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // Мост уже закрыт — сообщать некому.
        }
    }

    private void OnPtyExited(object? sender, PtyExitedEventArgs args) => _exitSignal.TrySetResult(args.ExitCode);

    /// <summary>
    /// Возвращает буфер пачки в пул и наблюдает исключение записи, если оно было:
    /// незамеченная сбойная задача всплыла бы как unobserved-исключение.
    /// </summary>
    private static void Release(PendingWrite write)
    {
        if (write.Task.IsFaulted)
        {
            _ = write.Task.Exception;
        }

        OutputAccumulator.Release(write.Buffer);
    }

    private readonly record struct PendingWrite(Task Task, ArraySegment<byte> Buffer);
}
