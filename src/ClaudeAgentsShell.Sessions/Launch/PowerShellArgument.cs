namespace ClaudeAgentsShell.Sessions.Launch;

/// <summary>
/// Экранирование аргумента для командной строки PowerShell.
/// Одинарные кавычки выбраны намеренно: внутри них <c>$</c>, обратная кавычка и двойные
/// кавычки — обычные символы, и путь вида <c>D:\src\$env</c> не превращается в подстановку.
/// </summary>
internal static class PowerShellArgument
{
    private static readonly char[] NeedsQuoting =
    [
        ' ', '\t', '\'', '"', '$', '`', ';', '&', '|', '(', ')', '{', '}', '[', ']',
        ',', '<', '>', '@', '#', '%', '*', '?', '^', '=', '!', '\r', '\n',
    ];

    /// <summary>Возвращает аргумент в виде, безопасном для вставки в строку команды.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return value.IndexOfAny(NeedsQuoting) < 0
            ? value
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
