using System.Buffers;
using System.Text.Json;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Имя сессии, найденное в просмотренной части транскрипта, и докуда она просмотрена.
/// </summary>
/// <param name="ScannedEnd">
/// Байтовое смещение сразу за последней целой просмотренной строкой: <c>0</c> или позиция
/// за <c>\n</c>. Отсюда продолжается просмотр, когда файл дописан.
/// </param>
/// <param name="CustomTitle">
/// Последняя запись <c>custom-title</c> (<c>/rename</c>): <c>null</c> — ни одной не встретилось,
/// пустая строка — имя сброшено, дальше решает <paramref name="AiTitle"/>.
/// </param>
/// <param name="AiTitle">Последняя запись <c>ai-title</c>; <c>null</c> — ни одной не встретилось.</param>
/// <remarks>
/// Записи хранятся порознь, а не готовым именем: при дописывании свежая <c>ai-title</c>
/// не должна перебить более раннюю, но действующую <c>custom-title</c>.
/// </remarks>
internal sealed record TitleRecords(long ScannedEnd, string? CustomTitle, string? AiTitle)
{
    /// <summary>Ничего не просмотрено.</summary>
    public static TitleRecords None { get; } = new(0, null, null);

    /// <summary>Имя сессии: действующее <c>custom-title</c>, иначе <c>ai-title</c>.</summary>
    public string? Name => CustomTitle is { Length: > 0 } custom ? custom : AiTitle;
}

/// <summary>
/// Ищет в хвосте транскрипта записи, которыми Claude Code называет сессию:
/// <c>{"type":"custom-title","customTitle":…}</c> и <c>{"type":"ai-title","aiTitle":…}</c>.
/// Обе «последняя побеждает».
/// </summary>
/// <remarks>
/// Весь файл не читается никогда: просматривается окно фиксированного размера от конца либо,
/// если файл только дописан, лишь дописанное (но не больше окна). Claude Code сам держит
/// эти записи у конца файла — после хода и при выходе он дописывает блок метаданных сессии
/// заново (<c>last-prompt</c>, <c>custom-title</c>, <c>ai-title</c>, <c>tag</c>). Перед разбором
/// JSON строка проверяется дешёвым поиском байтовой метки: в строковом значении JSON кавычки
/// экранированы, поэтому метка с голыми кавычками встречается только в самой структуре,
/// а корневой <c>type</c> всё равно сверяется после разбора.
/// </remarks>
internal static class TranscriptTail
{
    /// <summary>Окно не больше этого берётся из общего пула массивов.</summary>
    private const int PooledLimit = 64 * 1024;

    private static ReadOnlySpan<byte> CustomTitleMarker => "\"type\":\"custom-title\""u8;

    private static ReadOnlySpan<byte> AiTitleMarker => "\"type\":\"ai-title\""u8;

    /// <summary>
    /// Просматривает хвост файла и возвращает записи с учётом уже известных.
    /// </summary>
    /// <param name="stream">Открытый на чтение файл; позиция не важна.</param>
    /// <param name="size">Размер файла, по которому решалось, что читать.</param>
    /// <param name="carried">
    /// Найденное в прошлый раз. Вызывающий передаёт <see cref="TitleRecords.None"/>, если файл
    /// не просто дописан, а заменён.
    /// </param>
    /// <param name="window">Сколько байт от конца файла просматривать не больше чем.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="IOException">Файл не прочитался — решает вызывающий.</exception>
    public static async Task<TitleRecords> ScanAsync(
        FileStream stream,
        long size,
        TitleRecords carried,
        long window,
        CancellationToken cancellationToken)
    {
        if (size <= carried.ScannedEnd)
        {
            return carried;
        }

        var start = Math.Max(carried.ScannedEnd, size - window);

        // С сохранённой позиции окно начинается ровно на границе строки; от края окна —
        // посреди строки, и её обрывок не разбирается.
        var aligned = start == carried.ScannedEnd;
        var length = (int)(size - start);

        // Окно списка берётся из общего пула; мегабайтное окно одной сессии — мимо него: оно
        // читается раз на холодный старт вкладки, а вернувшись в пул, оседало бы там надолго.
        var pooled = length <= PooledLimit;
        var buffer = pooled ? ArrayPool<byte>.Shared.Rent(length) : new byte[length];
        try
        {
            stream.Seek(start, SeekOrigin.Begin);
            var read = 0;
            while (read < length)
            {
                var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            return Scan(buffer.AsMemory(0, read), start, aligned, carried);
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Разбирает прочитанное окно; вынесено из асинхронного метода ради <see cref="Span{T}"/>.</summary>
    private static TitleRecords Scan(ReadOnlyMemory<byte> window, long start, bool aligned, TitleRecords carried)
    {
        var span = window.Span;
        var position = 0;
        if (!aligned)
        {
            var first = span.IndexOf((byte)'\n');
            if (first < 0)
            {
                // Окно целиком — обрывок одной строки: записей имени в нём нет.
                return carried;
            }

            position = first + 1;
        }

        var custom = carried.CustomTitle;
        var ai = carried.AiTitle;
        while (position < span.Length)
        {
            var newline = span[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                // Последняя строка ещё дописывается: разберётся целиком в следующий раз.
                break;
            }

            Apply(window.Slice(position, newline), ref custom, ref ai);
            position += newline + 1;
        }

        return new TitleRecords(start + position, custom, ai);
    }

    private static void Apply(ReadOnlyMemory<byte> line, ref string? custom, ref string? ai)
    {
        var span = line.Span;
        var isCustom = span.IndexOf(CustomTitleMarker) >= 0;
        if (!isCustom && span.IndexOf(AiTitleMarker) < 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            if (type.ValueEquals("custom-title"))
            {
                // Пустое имя — сброс: дальше решает ai-title. Пробельное считается так же.
                if (ReadString(root, "customTitle") is { } value)
                {
                    custom = TranscriptLineParser.ShortenTitle(value) ?? string.Empty;
                }
            }
            else if (type.ValueEquals("ai-title")
                     && TranscriptLineParser.ShortenTitle(ReadString(root, "aiTitle")) is { } title)
            {
                ai = title;
            }
        }
        catch (JsonException)
        {
            // Битая строка пропускается: формат нестабилен (раздел 7 CLAUDE.md).
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
