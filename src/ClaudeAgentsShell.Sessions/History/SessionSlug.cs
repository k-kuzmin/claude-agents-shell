using System.Text;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Имя каталога транскриптов: путь рабочего каталога, где каждый не-буквенно-цифровой символ
/// заменён дефисом (раздел 5.2 ТЗ).
/// </summary>
/// <remarks>
/// «Буквенно-цифровой» здесь означает только ASCII: <c>[a-zA-Z0-9]</c>. Формулировка ТЗ
/// допускает и широкое чтение, но каталог создаёт не приложение, а Claude Code, и именно так
/// он его называет — <c>D--Portfolio-Projects-claude-agents-shell</c>. С <c>char.IsLetterOrDigit</c>
/// кириллица в пути осталась бы буквами, и мы искали бы несуществующий каталог.
/// </remarks>
public static class SessionSlug
{
    /// <summary>Имя каталога транскриптов для рабочего каталога сессии.</summary>
    public static string From(string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var normalized = Normalize(workingDirectory);
        var builder = new StringBuilder(normalized.Length);

        foreach (var symbol in normalized)
        {
            builder.Append(IsAsciiAlphanumeric(symbol) ? symbol : '-');
        }

        return builder.ToString();
    }

    private static bool IsAsciiAlphanumeric(char symbol) =>
        symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    /// <summary>
    /// Хвостовой разделитель и разное написание пути не должны давать разные каталоги.
    /// </summary>
    private static string Normalize(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            // Путь не разобрался — берём как есть: пустой каталог истории лучше исключения.
            return workingDirectory;
        }
    }
}
