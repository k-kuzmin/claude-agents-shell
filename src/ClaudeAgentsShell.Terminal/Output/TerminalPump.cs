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
        _accumulator = new OutputAccumulator(options.ReadBufferBytes);
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

        _pty.Exited -= OnPtyExited;

        // Снимаем backpressure до закрытия псевдоконсоли: иначе цикл может стоять в ожидании
        // подтверждения от страницы, перестать вычитывать пайп — и ClosePseudoConsole зависнет.
        lock (_sync)
        {
            _shuttingDown = true;
        }

        _shutdownSignal.TrySetResult();

        var ptyDispose = _pty.DisposeAsync().AsTask();

        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Читающий цикл не вышел за отведённое время — дальше освобождаем ресурсы всё равно.
            }
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

        _flushTimer.Dispose();

        lock (_sync)
        {
            while (_pending.Count > 0)
            {
                OutputAccumulator.Release(_pending.Dequeue().Buffer);
            }

            _accumulator.Dispose();
        }

        ArrayPool<byte>.Shared.Return(_readBuffer);
        _flushLock.Dispose();
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
                    OutputAccumulator.Release(_pending.Dequeue().Buffer);
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
                OutputAccumulator.Release(blocking.Buffer);
                throw;
            }
            catch (Exception)
            {
                // Страница не подтвердила запись (закрылась, упал рендерер) — чтение не останавливаем.
            }

            OutputAccumulator.Release(blocking.Buffer);
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

                if (_accumulator.Count == 0)
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

    private async Task NotifyExitedAsync()
    {
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

    private readonly record struct PendingWrite(Task Task, ArraySegment<byte> Buffer);
}
