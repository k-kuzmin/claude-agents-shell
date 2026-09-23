using System.Globalization;
using System.Text;

namespace ClaudeAgentsShell.App.History;

/// <summary>
/// Текст строки окна истории: дата «в духе интерфейса», короткий id и однострочный заголовок.
/// Чистые функции без часов и культуры машины — поэтому их можно проверять тестами.
/// </summary>
internal static class SessionHistoryFormat
{
    /// <summary>Сколько первых символов идентификатора сессии показывается в строке.</summary>
    public const int ShortIdLength = 8;

    // Интерфейс целиком русский; от CurrentCulture не зависим — у пользователя может стоять
    // английская локаль, и тогда строка вышла бы наполовину по-английски.
    private static readonly string[] MonthAbbreviations =
        ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    /// <summary>
    /// «сегодня 14:36», «вчера 21:48», «15 сен 11:24» в текущем году и «15 сен 2025» — раньше.
    /// </summary>
    /// <param name="modifiedUtc">Время последнего изменения транскрипта.</param>
    /// <param name="nowUtc">Текущее время.</param>
    /// <param name="zone">Часовой пояс пользователя: «сегодня» считается по его календарю.</param>
    public static string Date(DateTimeOffset modifiedUtc, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(modifiedUtc, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var day = local.Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (day == today)
        {
            return $"сегодня {time}";
        }

        if (day == today.AddDays(-1))
        {
            return $"вчера {time}";
        }

        var month = MonthAbbreviations[local.Month - 1];
        var dayOfMonth = local.Day.ToString(CultureInfo.InvariantCulture);

        // Будущая дата (часы разъехались) тоже попадает сюда и показывается как есть.
        return local.Year == today.Year
            ? $"{dayOfMonth} {month} {time}"
            : $"{dayOfMonth} {month} {local.Year.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Первые <see cref="ShortIdLength"/> символов идентификатора сессии.</summary>
    public static string ShortId(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return sessionId.Length <= ShortIdLength ? sessionId : sessionId[..ShortIdLength];
    }

    /// <summary>
    /// Первая непустая строка сообщения со схлопнутыми пробелами. Длину не режет — это
    /// делает обрезка текста в разметке по ширине окна. <c>null</c> — показывать нечего.
    /// </summary>
    public static string? SingleLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, 256));
        var pendingSpace = false;

        foreach (var ch in raw)
        {
            if (ch is '\n' or '\r')
            {
                if (builder.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
