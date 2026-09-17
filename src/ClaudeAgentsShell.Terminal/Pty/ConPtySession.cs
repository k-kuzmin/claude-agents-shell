using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Microsoft.Win32.SafeHandles;

namespace ClaudeAgentsShell.Terminal.Pty;

/// <summary>
/// Псевдоконсоль ConPTY с запущенной в ней оболочкой.
/// Владеет хэндлами, пайпами и процессом; освобождает их детерминированно.
/// </summary>
internal sealed class ConPtySession : IPtySession
{
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly ProcThreadAttributeList _attributes;
    private readonly SafeProcessHandle _process;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly WaitHandle _processWaitHandle;
    private readonly TaskCompletionSource _exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();

    private RegisteredWaitHandle? _exitRegistration;
    private int _exitCode = -1;
    private bool _exited;
    private bool _disposed;

    internal ConPtySession(
        SafePseudoConsoleHandle pseudoConsole,
        ProcThreadAttributeList attributes,
        SafeProcessHandle process,
        FileStream output,
        FileStream input)
    {
        _pseudoConsole = pseudoConsole;
        _attributes = attributes;
        _process = process;
        _output = output;
        _input = input;

        _processWaitHandle = new ProcessWaitHandle(process);

        // Завершение ловится ядром, а не опросом по таймеру.
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _processWaitHandle,
            static (state, _) => ((ConPtySession)state!).OnProcessExited(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return !_exited;
            }
        }
    }

    /// <inheritdoc />
    public int? ExitCode
    {
        get
        {
            lock (_sync)
            {
                return _exited ? _exitCode : null;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<PtyExitedEventArgs>? Exited;

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await _output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // Пайп закрыт при остановке сессии — для вызывающего это обычный конец потока.
            return 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                // Вкладку закрыли, пока пользователь печатал. Ввод девать некуда,
                // но падать обработчик события не имеет права.
                return;
            }
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // Оболочка уже завершилась: ввод просто некуда девать.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public void Resize(TerminalSize size)
    {
        if (!size.IsValid)
        {
            return;
        }

        // Ресайз приходит со страницы в произвольный момент, в том числе пока идёт закрытие
        // сессии. AddRef удерживает хэндл живым на время вызова.
        bool held = false;
        try
        {
            _pseudoConsole.DangerousAddRef(ref held);
            if (!held)
            {
                return;
            }

            var coord = new NativeMethods.Coord { X = (short)size.Cols, Y = (short)size.Rows };
            _ = NativeMethods.ResizePseudoConsole(_pseudoConsole.DangerousGetHandle(), coord);
        }
        catch (ObjectDisposedException)
        {
            // Псевдоконсоль уже закрыта — менять нечего.
        }
        finally
        {
            if (held)
            {
                _pseudoConsole.DangerousRelease();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Порядок важен: ClosePseudoConsole блокируется, пока ConPTY не допишет остаток вывода,
        // поэтому он вызывается первым и на отдельном потоке — читающий цикл в это время
        // продолжает разгребать пайп и получает EOF.
        await Task.Run(_pseudoConsole.Dispose).ConfigureAwait(false);

        // Даём читателю добрать хвост вывода до того, как пайпы закроются под ним.
        // Ожидание событийное: сигнал приходит из RegisterWaitForSingleObject, опроса нет.
        try
        {
            await _exitSignal.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Процесс не отдал управление за отведённое время — дальше освобождаем ресурсы всё равно.
        }

        if (_exitRegistration is { } registration)
        {
            // Unregister(null) не ждёт уже начавшийся колбэк. Ждём его явно, иначе
            // OnProcessExited успеет дёрнуть GetExitCodeProcess по освобождённому хэндлу.
            var callbacksFinished = new ManualResetEvent(false);
            registration.Unregister(callbacksFinished);

            if (callbacksFinished.WaitOne(TimeSpan.FromSeconds(1)))
            {
                callbacksFinished.Dispose();
            }

            // По таймауту событие намеренно не освобождается: пул сигналит его при
            // завершении колбэка, и закрытый хэндл мог бы быть переиспользован —
            // SetEvent ушёл бы в чужой объект синхронизации. Колбэк здесь дешёвый
            // и ограниченный, так что ветка практически недостижима; цена промаха —
            // один неосвобождённый хэндл события, который заберёт финализатор.
            _exitRegistration = null;
        }

        _input.Dispose();
        _output.Dispose();
        _attributes.Dispose();
        _processWaitHandle.Dispose();
        _process.Dispose();

        // _writeLock намеренно не освобождается: SemaphoreSlim без AvailableWaitHandle
        // в этом не нуждается, а его освобождение создавало бы гонку с вводом со страницы.
    }

    private void OnProcessExited()
    {
        int code = -1;
        try
        {
            if (NativeMethods.GetExitCodeProcess(_process, out uint raw) && raw != NativeMethods.StillActive)
            {
                code = unchecked((int)raw);
            }
        }
        catch (ObjectDisposedException)
        {
            // Сессию освободили одновременно с завершением процесса — код выхода уже не нужен.
        }

        lock (_sync)
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            _exitCode = code;
        }

        _exitSignal.TrySetResult();
        Exited?.Invoke(this, new PtyExitedEventArgs(code));
    }

    /// <summary>Ожидание завершения процесса без опроса: хэндл процесса сигналится ядром.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
