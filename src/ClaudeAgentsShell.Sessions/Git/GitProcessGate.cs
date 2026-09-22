namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Ограничивает число одновременно запущенных git на всё приложение. Очередь честная (FIFO):
/// место отдаётся самому раннему из ждущих. Отмена ожидания убирает запрос из очереди.
/// Ничем неуправляемым не владеет — ожидания это <see cref="TaskCompletionSource"/>.
/// </summary>
public sealed class GitProcessGate
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _free;

    /// <inheritdoc cref="GitProcessGate" />
    /// <param name="capacity">Сколько git может работать одновременно; не меньше 1.</param>
    public GitProcessGate(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _free = capacity;
    }

    /// <summary>Сколько запросов ждут места сейчас.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>Занимает место; освобождается вызовом <see cref="IDisposable.Dispose"/> у результата.</summary>
    /// <exception cref="OperationCanceledException">Ожидание отменено; место не занято.</exception>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_free > 0 && _waiters.Count == 0)
            {
                _free--;
                return new Lease(this);
            }

            node = _waiters.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        await using var registration = cancellationToken.Register(
            static state =>
            {
                var (gate, waiter) = ((GitProcessGate, LinkedListNode<TaskCompletionSource>))state!;
                gate.Cancel(waiter);
            },
            (this, node)).ConfigureAwait(false);

        await node.Value.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    private void Cancel(LinkedListNode<TaskCompletionSource> node)
    {
        lock (_lock)
        {
            // Место уже передано этому запросу — отмена опоздала, запрос его получит.
            if (node.List is null)
            {
                return;
            }

            _waiters.Remove(node);
        }

        node.Value.TrySetCanceled();
    }

    private void Release()
    {
        TaskCompletionSource? next = null;
        lock (_lock)
        {
            if (_waiters.First is { } first)
            {
                _waiters.RemoveFirst();
                next = first.Value;
            }
            else
            {
                _free++;
            }
        }

        next?.TrySetResult();
    }

    private sealed class Lease(GitProcessGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
