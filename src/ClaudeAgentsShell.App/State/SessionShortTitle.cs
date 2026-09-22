using System.Text;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Короткое имя сессии для полосы вкладок (раздел 6.3 ТЗ): первые слова первого сообщения
/// пользователя, одной строкой и разумной длины.
/// </summary>
/// <remarks>
/// Чистая функция без зависимостей, вынесенная из <see cref="SessionStateCoordinator"/>:
/// усечение проверяется тестом, а не запуском приложения.
/// </remarks>
internal static class SessionShortTitle
{
    /// <summary>Предельная длина результата — вместе с многоточием.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// Ниже этой границы обрывать по границе слова бессмысленно: от первого сообщения
    /// осталось бы одно-два слова. Тогда режется жёстко, по символам.
    /// </summary>
    private const int MinWordBoundary = MaxLength / 2;

    private const string Ellipsis = "…";

    /// <summary>Делает из первого сообщения пользователя короткое имя вкладки.</summary>
    /// <param name="rawTitle">
    /// Первое сообщение из транскрипта: может быть пустым, многострочным и сколь угодно длинным.
    /// </param>
    /// <returns>
    /// Имя не длиннее <see cref="MaxLength"/> либо <c>null</c>, если брать нечего — тогда
    /// заголовок вкладки не трогается и остаётся прежним («новая сессия»).
    /// </returns>
    public static string? Shorten(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        var line = FirstLine(rawTitle);
        if (line.Length == 0)
        {
            return null;
        }

        if (line.Length <= MaxLength)
        {
            return line;
        }

        // Место под многоточие вычитается заранее: результат обязан уложиться в MaxLength целиком.
        var budget = MaxLength - 1;
        var boundary = line.LastIndexOf(' ', budget);
        var cut = boundary >= MinWordBoundary ? boundary : budget;

        // Жёсткий рез мог прийтись на середину суррогатной пары — эмодзи в первом
        // сообщении не редкость, а половина пары рисуется квадратом.
        if (cut > 0 && char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return string.Concat(line.AsSpan(0, cut), Ellipsis);
    }

    /// <summary>
    /// Первая строка сообщения без переносов, табуляций и управляющих символов: пробельные
    /// пачки схлопываются в один пробел, края обрезаются.
    /// </summary>
    /// <remarks>
    /// Первые сообщения бывают в десятки килобайт (вставленный лог, кусок файла), поэтому
    /// перебор останавливается, как только набрано заметно больше <see cref="MaxLength"/>:
    /// хвост на результат уже не влияет.
    /// </remarks>
    private static string FirstLine(string raw)
    {
        var builder = new StringBuilder(MaxLength + 2);
        var pendingSpace = false;

        foreach (var ch in raw)
        {
            if (ch is '\n' or '\r')
            {
                if (builder.Length > 0)
                {
                    break;
                }

                // Сообщение, начатое с пустых строк, всё равно должно дать имя: берётся
                // первая непустая строка, а не пустота перед ней.
                continue;
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                // Ведущие пробелы отбрасываются, внутренние — схлопываются, хвостовой
                // так и не дописывается, если строка на нём кончилась.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);

            if (builder.Length > MaxLength + 1)
            {
                break;
            }
        }

        return builder.ToString();
    }
}
