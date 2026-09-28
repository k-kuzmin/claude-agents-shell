namespace ClaudeAgentsShell.Sessions.Files;

/// <summary>
/// Пропускает из аргументов агента только локальные пути — до любого обращения к диску:
/// <c>Directory.Exists(@"\\host\share")</c> или <c>@"\??\UNC\host\share"</c> уже открывает
/// SMB-соединение и отдаёт хосту NTLM-хеш пользователя, а инструменты MCP разрешены заранее.
/// Список разрешённого, а не запрещённого: префиксов пространств имён Windows слишком много.
/// </summary>
internal static class LocalPathGuard
{
    /// <summary>Текст ошибки аргументов для агента.</summary>
    public const string RejectedText =
        "Only local paths are allowed: an absolute path must start with a drive letter (C:\\...), "
        + "a relative path must not start with \\ or / and must not contain ':'. "
        + "Network (UNC), device and NT paths are rejected.";

    /// <summary>
    /// Путь допустим: <c>null</c> (не задан); абсолютный вида <c>&lt;буква&gt;:\…</c> или
    /// <c>&lt;буква&gt;:/…</c> без других <c>:</c>; относительный, не начинающийся с <c>\</c>
    /// или <c>/</c> и без <c>:</c> (иначе <c>C:foo</c> — путь от текущего каталога диска,
    /// а <c>a:b</c> — альтернативный поток NTFS).
    /// </summary>
    public static bool IsLocal(string? path)
    {
        if (path is null)
        {
            return true;
        }

        var text = path.TrimStart();
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && IsSeparator(text[2]))
        {
            return !text.AsSpan(3).Contains(':');
        }

        return text.Length > 0 && !IsSeparator(text[0]) && !text.Contains(':', StringComparison.Ordinal);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
