using System.Text;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Запись сгенерированного файла через временный: сессия могла открыть прежний
/// (по <c>--settings</c> или <c>--mcp-config</c>), и надорванный файл хуже устаревшего.
/// </summary>
internal static class AtomicTextFile
{
    private const string TempSuffix = ".tmp";

    /// <summary>Пишет <paramref name="content"/> в UTF-8 без BOM и подменяет файл целиком.</summary>
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + TempSuffix;
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
