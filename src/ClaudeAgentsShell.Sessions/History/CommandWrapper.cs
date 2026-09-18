namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Разбор обёртки слэш-команды в транскрипте: сессия, начатая командой, приходит строкой вида
/// <c>&lt;command-name&gt;/work&lt;/command-name&gt;&lt;command-args&gt;WO-15917 препрод&lt;/command-args&gt;</c>.
/// Для вкладки это и есть первое сообщение пользователя (раздел 6.3 ТЗ).
/// </summary>
/// <remarks>
/// Формат <c>.jsonl</c> нестабилен, поэтому здесь нет разбора XML: берётся текст между открывающим
/// и первым закрывающим тегом. Нет тега, нет закрытия, пустое или непохожее на имя команды
/// содержимое — заголовка просто нет, исключений не бывает (раздел 7 CLAUDE.md).
/// </remarks>
internal static class CommandWrapper
{
    private const string NameOpen = "<command-name>";
    private const string NameClose = "</command-name>";
    private const string ArgsOpen = "<command-args>";
    private const string ArgsClose = "</command-args>";

    /// <summary>
    /// Имя вкладки по обёртке команды: сама команда и её аргументы, если они есть.
    /// </summary>
    /// <param name="text">Текст сообщения пользователя целиком.</param>
    /// <returns>
    /// Например <c>/work WO-15917 препрод</c>; <c>null</c>, если обёртки нет или она не разобралась.
    /// Аргументы включены намеренно: вкладок с одной и той же командой бывает несколько, и без них
    /// они неразличимы в полосе вкладок.
    /// </returns>
    public static string? Title(ReadOnlySpan<char> text)
    {
        var name = Content(text, NameOpen, NameClose);

        // Имя команды начинается со слэша. Эта же проверка отсекает вложенную обёртку и мусор:
        // разбирать их незачем, достаточно не выдать за заголовок.
        if (name.IsEmpty || name[0] != '/')
        {
            return null;
        }

        var args = Content(text, ArgsOpen, ArgsClose);
        return args.IsEmpty ? new string(name) : string.Concat(name, " ", args);
    }

    /// <summary>Содержимое между первым открывающим тегом и ближайшим к нему закрывающим.</summary>
    private static ReadOnlySpan<char> Content(ReadOnlySpan<char> text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return default;
        }

        var rest = text[(start + open.Length)..];
        var end = rest.IndexOf(close, StringComparison.Ordinal);
        return end < 0 ? default : rest[..end].Trim();
    }
}
