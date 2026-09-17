using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.Fakes;

/// <summary>
/// Мост-заглушка. Запоминает отправленные пачки и, если подтверждения выключены,
/// держит <see cref="WriteOutputAsync"/> незавершённым — так проверяется backpressure.
/// </summary>
internal sealed class FakeTerminalBridge : ITerminalBridge
{
    private readonly List<byte[]> _batches = [];
    private readonly Dictionary<string, List<byte>> _byTerminal = new(StringComparer.Ordinal);
    private readonly Queue<TaskCompletionSource> _pending = new();
    private readonly object _sync = new();

    public FakeTerminalBridge(bool acknowledgeImmediately = true)
    {
        AcknowledgeImmediately = acknowledgeImmediately;
    }

    public bool AcknowledgeImmediately { get; }

    public event EventHandler<TerminalInputEventArgs>? InputReceived;

    public event EventHandler<TerminalResizeEventArgs>? ResizeRequested;

    public event EventHandler<TerminalReadyEventArgs>? TerminalReady;

    public List<int> ExitCodes { get; } = [];

    /// <summary>Вкладки, которые мост просил страницу уничтожить.</summary>
    public List<string> ClosedTerminals { get; } = [];

    /// <summary>Вкладки, созданные на странице.</summary>
    public List<string> CreatedTerminals { get; } = [];

    /// <summary>Вкладки, которые мост просил страницу показать, по порядку запросов.</summary>
    public List<string> ShownTerminals { get; } = [];

    /// <summary>Байты, отправленные в конкретную вкладку: по ним видно маршрутизацию вывода.</summary>
    public byte[] BytesFor(TerminalId terminalId)
    {
        lock (_sync)
        {
            return _byTerminal.TryGetValue(terminalId.Value, out var bytes) ? bytes.ToArray() : [];
        }
    }

    public IReadOnlyList<byte[]> Batches
    {
        get
        {
            lock (_sync)
            {
                return _batches.ToArray();
            }
        }
    }

    public byte[] AllBytes
    {
        get
        {
            lock (_sync)
            {
                return _batches.SelectMany(static b => b).ToArray();
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public void AcknowledgeNext()
    {
        TaskCompletionSource? next;
        lock (_sync)
        {
            next = _pending.Count > 0 ? _pending.Dequeue() : null;
        }

        next?.TrySetResult();
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask CreateTerminalAsync(TerminalId terminalId, string title, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            CreatedTerminals.Add(terminalId.Value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ShowTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ShownTerminals.Add(terminalId.Value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ClosedTerminals.Add(terminalId.Value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutputAsync(TerminalId terminalId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Копия снимается синхронно: вызывающий вправе вернуть буфер в пул сразу после вызова.
        TaskCompletionSource? acknowledgement = null;

        lock (_sync)
        {
            byte[] copy = payload.ToArray();
            _batches.Add(copy);

            if (!_byTerminal.TryGetValue(terminalId.Value, out var bytes))
            {
                bytes = [];
                _byTerminal[terminalId.Value] = bytes;
            }

            bytes.AddRange(copy);

            if (!AcknowledgeImmediately)
            {
                acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Enqueue(acknowledgement);
            }
        }

        return acknowledgement is null ? ValueTask.CompletedTask : new ValueTask(acknowledgement.Task);
    }

    public ValueTask NotifyExitedAsync(TerminalId terminalId, int exitCode, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ExitCodes.Add(exitCode);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseInput(TerminalId terminalId, ReadOnlyMemory<byte> data) =>
        InputReceived?.Invoke(this, new TerminalInputEventArgs(terminalId, data));

    public void RaiseResize(TerminalId terminalId, TerminalSize size) =>
        ResizeRequested?.Invoke(this, new TerminalResizeEventArgs(terminalId, size));

    public void RaiseReady(TerminalId terminalId) =>
        TerminalReady?.Invoke(this, new TerminalReadyEventArgs(terminalId));
}
