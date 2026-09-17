using System.Threading.Channels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.Fakes;

/// <summary>
/// Псевдоконсоль-заглушка: отдаёт заранее подготовленные чанки. Нужна, чтобы проверять
/// склейку и backpressure без реального процесса.
/// </summary>
internal sealed class FakePtySession : IPtySession
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte> _written = [];
    private readonly List<TerminalSize> _resizes = [];
    private readonly object _sync = new();

    private byte[]? _current;
    private int _offset;
    private int _chunksConsumed;
    private int _disposeCount;

    public bool IsRunning { get; private set; } = true;

    public int? ExitCode { get; private set; }

    public event EventHandler<PtyExitedEventArgs>? Exited;

    /// <summary>Сколько чанков читающий цикл успел забрать. По нему видно, что он приостановлен.</summary>
    public int ChunksConsumed => Volatile.Read(ref _chunksConsumed);

    /// <summary>Псевдоконсоль освобождена — то, что должно случиться при выходе оболочки.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    /// <summary>Сколько раз освобождали: освобождение обязано быть идемпотентным.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Сколько длится освобождение — для проверки параллельного закрытия вкладок.</summary>
    public TimeSpan DisposeDelay { get; init; }

    public IReadOnlyList<byte> WrittenInput
    {
        get
        {
            lock (_sync)
            {
                return _written.ToArray();
            }
        }
    }

    public IReadOnlyList<TerminalSize> Resizes
    {
        get
        {
            lock (_sync)
            {
                return _resizes.ToArray();
            }
        }
    }

    public void Emit(params byte[] data) => _chunks.Writer.TryWrite(data);

    public void EndOfStream() => _chunks.Writer.TryComplete();

    public void RaiseExited(int exitCode)
    {
        IsRunning = false;
        ExitCode = exitCode;
        Exited?.Invoke(this, new PtyExitedEventArgs(exitCode));
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null || _offset >= _current.Length)
        {
            try
            {
                _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            _offset = 0;
            Interlocked.Increment(ref _chunksConsumed);
        }

        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _written.AddRange(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(TerminalSize size)
    {
        lock (_sync)
        {
            _resizes.Add(size);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _chunks.Writer.TryComplete();

        // Закрытие псевдоконсоли гасит процесс оболочки — фейк обязан вести себя так же,
        // иначе помпа впустую ждёт сигнала выхода до истечения таймаута.
        if (IsRunning)
        {
            RaiseExited(0);
        }

        if (DisposeDelay > TimeSpan.Zero)
        {
            await Task.Delay(DisposeDelay).ConfigureAwait(false);
        }
    }
}
