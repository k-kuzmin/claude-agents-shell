using System.Text;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Дополнительные аргументы <c>claude</c> (раздел 6.5 ТЗ): пользователь вводит их одной строкой,
/// а <c>projects.json</c> хранит список. Разбор и сборка — чистые операции над строкой,
/// поэтому живут отдельно от диалога и проверяются тестами без окна.
/// </summary>
/// <remarks>
/// Правила намеренно проще, чем у разбора командной строки в MSVCRT:
/// <list type="bullet">
/// <item>пробелы и табуляции разделяют аргументы;</item>
/// <item>двойная кавычка открывает и закрывает участок, внутри которого пробелы обычные;</item>
/// <item>две кавычки подряд внутри кавычек — это одна литеральная кавычка;</item>
/// <item>обратный слэш экранирующим символом <b>не</b> считается.</item>
/// </list>
/// Последний пункт — сознательное отступление: аргументы этого приложения почти всегда содержат
/// пути Windows, и <c>"C:\repo\"</c> обязан выжить, а по правилам MSVCRT он съел бы закрывающую
/// кавычку. Незакрытая кавычка не считается ошибкой: накопленный аргумент просто закрывается
/// концом строки, иначе диалог ругался бы на каждое нажатие клавиши.
/// </remarks>
public static class ExtraArgsSyntax
{
    private const char Quote = '"';

    /// <summary>
    /// Разбирает строку пользователя в список аргументов.
    /// Пустая строка и строка из пробелов дают пустой список.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();

        // Отдельный флаг, а не длина накопителя: пустой аргумент «""» тоже аргумент,
        // и без флага он потерялся бы при сбросе.
        var started = false;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var symbol = text[i];

            if (symbol == Quote)
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == Quote)
                {
                    current.Append(Quote);
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(symbol))
            {
                Flush(result, current, ref started);
                continue;
            }

            current.Append(symbol);
            started = true;
        }

        Flush(result, current, ref started);
        return result;
    }

    /// <summary>
    /// Собирает список аргументов обратно в строку для поля ввода.
    /// Аргумент берётся в кавычки, только если без них он развалился бы при разборе:
    /// иначе повторное открытие диалога переписывало бы значение, которого никто не трогал.
    /// </summary>
    public static string Format(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var arg in args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            AppendArgument(builder, arg);
        }

        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string arg)
    {
        if (!NeedsQuotes(arg))
        {
            builder.Append(arg);
            return;
        }

        builder.Append(Quote);
        foreach (var symbol in arg)
        {
            if (symbol == Quote)
            {
                builder.Append(Quote);
            }

            builder.Append(symbol);
        }

        builder.Append(Quote);
    }

    private static bool NeedsQuotes(string arg)
    {
        if (arg.Length == 0)
        {
            return true;
        }

        foreach (var symbol in arg)
        {
            if (symbol == Quote || char.IsWhiteSpace(symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static void Flush(List<string> result, StringBuilder current, ref bool started)
    {
        if (started)
        {
            result.Add(current.ToString());
            current.Clear();
            started = false;
        }
    }
}
