using System.Buffers;

namespace ClaudeAgentsShell.Terminal.Output;

/// <summary>
/// Растущий буфер пачки вывода. Байты копятся без лишних копирований, память берётся из
/// <see cref="ArrayPool{T}"/>. При сбросе буфер не копируется, а отцепляется целиком:
/// вызывающий отдаёт его мосту и возвращает в пул, когда запись подтверждена.
/// </summary>
internal sealed class OutputAccumulator : IDisposable
{
    private readonly int _initialCapacity;
    private byte[] _buffer;
    private int _count;

    internal OutputAccumulator(int initialCapacity)
    {
        _initialCapacity = Math.Max(initialCapacity, 1024);
        _buffer = ArrayPool<byte>.Shared.Rent(_initialCapacity);
    }

    /// <summary>Сколько байтов накоплено с прошлого сброса.</summary>
    internal int Count => _count;

    internal void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>
    /// Отцепляет накопленное и начинает копить заново. Возвращённый сегмент нужно вернуть
    /// в пул через <see cref="Release"/> после того, как мост его отработал.
    /// </summary>
    internal ArraySegment<byte> Detach()
    {
        var segment = new ArraySegment<byte>(_buffer, 0, _count);
        _buffer = ArrayPool<byte>.Shared.Rent(_initialCapacity);
        _count = 0;
        return segment;
    }

    internal static void Release(ArraySegment<byte> segment)
    {
        if (segment.Array is { } array)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _count = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int capacity = Math.Max(required, _buffer.Length * 2);
        byte[] grown = ArrayPool<byte>.Shared.Rent(capacity);
        _buffer.AsSpan(0, _count).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
