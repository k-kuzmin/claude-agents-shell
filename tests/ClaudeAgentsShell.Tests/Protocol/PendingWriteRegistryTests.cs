using ClaudeAgentsShell.Terminal.Protocol;
using Xunit;

namespace ClaudeAgentsShell.Tests.Protocol;

public sealed class PendingWriteRegistryTests
{
    private readonly PendingWriteRegistry _registry = new();

    [Fact]
    public void Подтверждение_завершает_свою_запись()
    {
        long sequence = _registry.Reserve(out var acknowledged);

        Assert.False(acknowledged.IsCompleted);

        _registry.CompleteUpTo(sequence);

        Assert.True(acknowledged.IsCompleted);
        Assert.Equal(0, _registry.Count);
    }

    /// <summary>
    /// Ключевое свойство: соответствие держится на номере пачки, а не на порядке очереди.
    /// Потерянная квитанция раньше сдвигала бы соответствие навсегда — каждая следующая
    /// завершала бы чужую, более раннюю запись.
    /// </summary>
    [Fact]
    public void Потерянная_квитанция_не_сдвигает_соответствие()
    {
        long first = _registry.Reserve(out var firstWrite);
        long second = _registry.Reserve(out var secondWrite);
        long third = _registry.Reserve(out var thirdWrite);

        // Квитанция на вторую пачку потерялась, пришла сразу третья.
        _registry.CompleteUpTo(third);

        Assert.True(firstWrite.IsCompleted);
        Assert.True(secondWrite.IsCompleted);
        Assert.True(thirdWrite.IsCompleted);
        Assert.Equal(0, _registry.Count);
        Assert.True(first < second && second < third);
    }

    [Fact]
    public void Подтверждение_ранней_пачки_не_трогает_поздние()
    {
        long first = _registry.Reserve(out var firstWrite);
        _registry.Reserve(out var secondWrite);

        _registry.CompleteUpTo(first);

        Assert.True(firstWrite.IsCompleted);
        Assert.False(secondWrite.IsCompleted);
        Assert.Equal(1, _registry.Count);
    }

    [Fact]
    public void Брошенное_ожидание_не_забирает_чужую_квитанцию()
    {
        long abandoned = _registry.Reserve(out var abandonedWrite);
        long next = _registry.Reserve(out var nextWrite);

        // Вызывающий отказался ждать первую запись (например, по отмене).
        _registry.Abandon(abandoned);

        _registry.CompleteUpTo(next);

        Assert.True(nextWrite.IsCompleted);
        Assert.False(abandonedWrite.IsCompleted);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void Отпускание_завершает_всё_и_очищает_учёт()
    {
        _registry.Reserve(out var first);
        _registry.Reserve(out var second);

        // Вкладка закрылась или упал рендерер: подтверждений больше не будет.
        _registry.ReleaseAll();

        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void Повторное_подтверждение_безвредно()
    {
        long sequence = _registry.Reserve(out var acknowledged);

        _registry.CompleteUpTo(sequence);
        _registry.CompleteUpTo(sequence);
        _registry.ReleaseAll();

        Assert.True(acknowledged.IsCompleted);
    }

    /// <summary>
    /// После освобождения подтверждений не будет никогда: новая запись обязана получить
    /// уже завершённое ожидание. Иначе она припарковалась бы навсегда, и по достижении
    /// MaxPendingWrites чтение из PTY встало бы без возможности возобновиться.
    /// </summary>
    [Fact]
    public void После_освобождения_новая_запись_не_паркуется()
    {
        _registry.ReleaseAll();

        _registry.Reserve(out var afterRelease);

        Assert.True(afterRelease.IsCompleted);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void Защёлка_не_снимается_подтверждениями()
    {
        _registry.ReleaseAll();

        long sequence = _registry.Reserve(out _);
        _registry.CompleteUpTo(sequence);
        _registry.Abandon(sequence);

        _registry.Reserve(out var later);

        Assert.True(_registry.IsReleased);
        Assert.True(later.IsCompleted);
    }

    [Fact]
    public void Незащёлкнутый_реестр_паркует_запись()
    {
        _registry.Reserve(out var pending);

        Assert.False(pending.IsCompleted);
        Assert.False(_registry.IsReleased);
    }

    [Fact]
    public void Номера_пачек_растут_и_после_освобождения()
    {
        long before = _registry.Reserve(out _);
        _registry.ReleaseAll();
        long after = _registry.Reserve(out _);

        Assert.True(after > before);
    }

    [Fact]
    public void Номера_пачек_не_повторяются()
    {
        var sequences = new HashSet<long>();

        for (int i = 0; i < 100; i++)
        {
            Assert.True(sequences.Add(_registry.Reserve(out _)));
        }

        Assert.Equal(100, _registry.Count);
    }
}
