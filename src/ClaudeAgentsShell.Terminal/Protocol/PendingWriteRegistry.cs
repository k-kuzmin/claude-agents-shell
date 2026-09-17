namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Учёт незавершённых записей одной вкладки: пачке выдаётся номер, по которому потом
/// сопоставляется квитанция страницы. Счёт по порядку очереди здесь не годится — одна
/// потерянная квитанция сдвинула бы соответствие навсегда, и каждая следующая завершала бы
/// чужую, более раннюю запись.
/// <para>
/// Это бухгалтерия протокола, а не деталь WebView2, поэтому живёт в слое терминала
/// и покрывается тестами отдельно от моста.
/// </para>
/// </summary>
public sealed class PendingWriteRegistry
{
    private readonly Dictionary<long, TaskCompletionSource> _items = [];
    private readonly object _sync = new();

    private long _lastSequence = -1;

    /// <summary>Сколько записей ждёт подтверждения.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Резервирует номер под очередную пачку и отдаёт ожидание, которое завершится,
    /// когда страница подтвердит запись.
    /// </summary>
    public long Reserve(out Task acknowledged)
    {
        lock (_sync)
        {
            long sequence = ++_lastSequence;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _items[sequence] = completion;
            acknowledged = completion.Task;
            return sequence;
        }
    }

    /// <summary>
    /// Завершает пачку с этим номером и все более ранние: страница пишет их по порядку,
    /// поэтому подтверждение поздней пачки означает, что ранние уже записаны.
    /// Благодаря этому потерянная квитанция не оставляет ожидание висеть навсегда.
    /// </summary>
    public void CompleteUpTo(long sequence)
    {
        List<TaskCompletionSource>? completed = null;

        lock (_sync)
        {
            foreach (long key in _items.Keys.ToArray())
            {
                if (key <= sequence && _items.Remove(key, out var completion))
                {
                    (completed ??= []).Add(completion);
                }
            }
        }

        CompleteAll(completed);
    }

    /// <summary>
    /// Снимает ожидание с учёта, не завершая его: вызывающий отказался ждать.
    /// Без этого следующая квитанция завершила бы брошенную запись вместо актуальной.
    /// </summary>
    public void Abandon(long sequence)
    {
        lock (_sync)
        {
            _items.Remove(sequence);
        }
    }

    /// <summary>
    /// Отпускает все ожидания: подтверждений больше не будет — вкладка закрыта,
    /// мост освобождён или упал рендерер. Иначе чтение из PTY встало бы навсегда.
    /// </summary>
    public void ReleaseAll()
    {
        List<TaskCompletionSource>? completed = null;

        lock (_sync)
        {
            foreach (var completion in _items.Values)
            {
                (completed ??= []).Add(completion);
            }

            _items.Clear();
        }

        CompleteAll(completed);
    }

    private static void CompleteAll(List<TaskCompletionSource>? completions)
    {
        if (completions is null)
        {
            return;
        }

        // Завершаем вне блокировки: продолжения ожидающих не должны выполняться под ней.
        foreach (var completion in completions)
        {
            completion.TrySetResult();
        }
    }
}
