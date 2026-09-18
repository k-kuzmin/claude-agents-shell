using System.Text;
using System.Text.Json;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>Что удалось вытащить из одной строки <c>.jsonl</c>.</summary>
/// <param name="Title">Первое сообщение пользователя, если эта строка им и оказалась.</param>
/// <param name="Branch">Ветка git, записанная Claude Code в строку.</param>
internal readonly record struct TranscriptLine(string? Title, string? Branch);

/// <summary>
/// Разбор строки транскрипта. Формат нестабилен, поэтому здесь нет ни одной обязательной
/// структуры: не разобралось — строка просто пропускается (раздел 8 ТЗ).
/// </summary>
internal static class TranscriptLineParser
{
    /// <summary>Длина заголовка вкладки: длиннее в полосу вкладок всё равно не влезает.</summary>
    private const int TitleLimit = 120;

    public static TranscriptLine Parse(string line)
    {
        if (line.Length == 0)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var root = document.RootElement;
            var branch = ReadString(root, "gitBranch");

            return IsFirstUserMessageCandidate(root)
                ? new TranscriptLine(ExtractTitle(root), branch)
                : new TranscriptLine(null, branch);
        }
        catch (JsonException)
        {
            // Битая строка пропускается, разбор продолжается (раздел 8 ТЗ).
            return default;
        }
    }

    private static bool IsFirstUserMessageCandidate(JsonElement root)
    {
        if (!string.Equals(ReadString(root, "type"), "user", StringComparison.Ordinal))
        {
            return false;
        }

        // Ветка сабагента и служебные строки заголовком вкладки быть не могут.
        return !IsTrue(root, "isSidechain") && !IsTrue(root, "isMeta");
    }

    private static string? ExtractTitle(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => FirstTextBlock(content),
            _ => null,
        };

        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return null;
        }

        // Всё в угловых скобках — служебная обёртка. Единственная, которая несёт имя вкладки, —
        // слэш-команда: она и есть первое сообщение пользователя (раздел 6.3 ТЗ).
        if (trimmed[0] != '<')
        {
            return Shorten(trimmed);
        }

        return IsHumanPrompt(root) ? Shorten(CommandWrapper.Title(trimmed)) : null;
    }

    /// <summary>
    /// Строка — запрос, отправленный пользователем агенту, а не команда самой оболочки
    /// Claude Code (<c>/clear</c>, <c>/model</c>, <c>/mcp</c>).
    /// </summary>
    /// <remarks>
    /// Обе разновидности приходят одной и той же обёрткой <c>&lt;command-name&gt;</c>, и различает
    /// их только <c>origin</c>: у команды оболочки его нет — она никуда не отправляется. Замер по
    /// транскриптам пользователя: без этой проверки 59 вкладок вместо первого сообщения назывались
    /// бы <c>/clear</c>. Пропадёт поле из формата — заголовок из команды просто перестанет
    /// извлекаться, как было до этой правки, и вкладка останется «новой сессией».
    /// </remarks>
    private static bool IsHumanPrompt(JsonElement root) =>
        root.TryGetProperty("origin", out var origin)
        && origin.ValueKind == JsonValueKind.Object
        && string.Equals(ReadString(origin, "kind"), "human", StringComparison.Ordinal);

    private static string? FirstTextBlock(JsonElement content)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(block, "type"), "text", StringComparison.Ordinal)
                && ReadString(block, "text") is { } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <inheritdoc cref="Shorten(ReadOnlySpan{char})" />
    private static string? Shorten(string? text) =>
        text is null ? null : Shorten(text.AsSpan());

    /// <summary>
    /// Схлопывает сообщение в одну строку и обрезает до <see cref="TitleLimit"/>: в полосу вкладок
    /// длиннее всё равно не влезает, а переводы строк в заголовке выглядят как мусор.
    /// </summary>
    private static string? Shorten(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        if (trimmed.IsEmpty)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(trimmed.Length, TitleLimit));
        var lastWasSpace = false;

        foreach (var symbol in trimmed)
        {
            var isSpace = char.IsWhiteSpace(symbol);
            if (isSpace && lastWasSpace)
            {
                continue;
            }

            builder.Append(isSpace ? ' ' : symbol);
            lastWasSpace = isSpace;

            if (builder.Length == TitleLimit)
            {
                break;
            }
        }

        var result = builder.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;
}
