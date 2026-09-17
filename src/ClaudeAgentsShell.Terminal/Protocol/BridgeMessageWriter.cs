using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Сборка исходящих сообщений моста. <see cref="Out"/> — горячий путь: строка собирается
/// за одну аллокацию через <see cref="string.Create{TState}"/>, base64 пишется прямо в её буфер
/// методом <see cref="Convert.TryToBase64Chars"/>. Ни промежуточных строк, ни сериализатора.
/// </summary>
public sealed class BridgeMessageWriter : IBridgeMessageWriter
{
    private const string OutHead = "{\"type\":\"out\",\"id\":\"";
    private const string OutSeq = "\",\"seq\":";
    private const string OutMiddle = ",\"b64\":\"";
    private const string Tail = "\"}";

    /// <inheritdoc />
    public unsafe string Out(TerminalId terminalId, long sequence, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        string id = JsonStringEscape.Escape(terminalId.Value);
        int base64Length = Base64Length(payload.Length);
        int total = OutHead.Length + id.Length + OutSeq.Length + CountDigits(sequence)
            + OutMiddle.Length + base64Length + Tail.Length;

        // Указатель на полезную нагрузку живёт ровно столько, сколько работает fixed:
        // string.Create вызывает свой обработчик синхронно, до выхода из блока.
        fixed (byte* pinned = payload)
        {
            var state = new OutState(id, sequence, (IntPtr)pinned, payload.Length);
            return string.Create(total, state, static (span, s) =>
            {
                int offset = 0;
                Append(span, ref offset, OutHead);
                Append(span, ref offset, s.Id);
                Append(span, ref offset, OutSeq);

                if (!s.Sequence.TryFormat(span[offset..], out int sequenceLength, default, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException("Не хватило места под номер пачки в буфере сообщения.");
                }

                offset += sequenceLength;
                Append(span, ref offset, OutMiddle);

                if (s.Length > 0)
                {
                    var source = MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.AsRef<byte>((void*)s.Payload),
                        s.Length);

                    if (!Convert.TryToBase64Chars(source, span[offset..], out int written))
                    {
                        throw new InvalidOperationException("Не хватило места под base64 в буфере сообщения.");
                    }

                    offset += written;
                }

                Append(span, ref offset, Tail);
            });
        }
    }

    /// <inheritdoc />
    public string Create(TerminalId terminalId, string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        // Единственное место, где в сообщение попадает произвольный пользовательский текст.
        return string.Concat(
            "{\"type\":\"create\",\"id\":\"",
            JsonStringEscape.Escape(terminalId.Value),
            "\",\"title\":\"",
            JsonEncodedText.Encode(title).Value,
            "\"}");
    }

    /// <inheritdoc />
    public string Show(TerminalId terminalId) =>
        string.Concat("{\"type\":\"show\",\"id\":\"", JsonStringEscape.Escape(terminalId.Value), "\"}");

    /// <inheritdoc />
    public string Close(TerminalId terminalId) =>
        string.Concat("{\"type\":\"close\",\"id\":\"", JsonStringEscape.Escape(terminalId.Value), "\"}");

    /// <inheritdoc />
    public string Exited(TerminalId terminalId, int exitCode) =>
        string.Concat(
            "{\"type\":\"exited\",\"id\":\"",
            JsonStringEscape.Escape(terminalId.Value),
            "\",\"code\":",
            exitCode.ToString(CultureInfo.InvariantCulture),
            "}");

    internal static int Base64Length(int byteCount) => ((byteCount + 2) / 3) * 4;

    private static int CountDigits(long value)
    {
        int digits = 1;
        while ((value /= 10) != 0)
        {
            digits++;
        }

        return digits;
    }

    private static void Append(Span<char> destination, ref int offset, string value)
    {
        value.AsSpan().CopyTo(destination[offset..]);
        offset += value.Length;
    }

    private readonly struct OutState(string id, long sequence, IntPtr payload, int length)
    {
        internal string Id { get; } = id;

        internal long Sequence { get; } = sequence;

        internal IntPtr Payload { get; } = payload;

        internal int Length { get; } = length;
    }
}

/// <summary>
/// Экранирование значения, попадающего в JSON-строку. Идентификаторы вкладок состоят из
/// латиницы и цифр, поэтому у них быстрый путь без единой аллокации.
/// </summary>
internal static class JsonStringEscape
{
    internal static string Escape(string value)
    {
        foreach (char c in value)
        {
            bool safe = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
            if (!safe)
            {
                return JsonEncodedText.Encode(value).Value;
            }
        }

        return value;
    }
}
