namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Бюджет байтов на чтение неотслеживаемых файлов одного оглавления. Потокобезопасен:
/// файлы считаются параллельно.
/// </summary>
public sealed class DiffReadBudget
{
    private long _remaining;

    /// <inheritdoc cref="DiffReadBudget" />
    /// <param name="bytes">Сколько байт можно прочитать всего.</param>
    public DiffReadBudget(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _remaining = bytes;
    }

    /// <summary>Сколько байт ещё осталось.</summary>
    public long Remaining => Interlocked.Read(ref _remaining);

    /// <summary>Резервирует байты под чтение файла; не хватает — ничего не списывает и возвращает <c>false</c>.</summary>
    public bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var current = Interlocked.Read(ref _remaining);
            if (current < bytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remaining, current - bytes, current) == current)
            {
                return true;
            }
        }
    }
}
