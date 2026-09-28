namespace ClaudeAgentsShell.Sessions.Files;

/// <summary>
/// Отсекает сетевые и device-пути из аргументов агента до любого обращения к диску:
/// <c>Directory.Exists(@"\\host\share")</c> уже открывает SMB-соединение и отдаёт хосту
/// NTLM-хеш пользователя, а инструменты MCP разрешены заранее.
/// </summary>
internal static class LocalPathGuard
{
    /// <summary>Текст ошибки аргументов для агента.</summary>
    public const string RejectedText = "Network (UNC) and device paths are not allowed; use a local path.";

    /// <summary>
    /// Путь начинается с двух разделителей в любом сочетании: UNC (<c>\\host\share</c>,
    /// <c>//host/share</c>) и device-пути (<c>\\?\</c>, <c>\\.\</c>).
    /// </summary>
    public static bool IsNetworkOrDevice(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var text = path.TrimStart();
        return text.Length >= 2 && IsSeparator(text[0]) && IsSeparator(text[1]);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
