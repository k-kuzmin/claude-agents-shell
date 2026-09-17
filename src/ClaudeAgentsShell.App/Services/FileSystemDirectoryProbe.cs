using System.IO;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Проверка каталога на диске.</summary>
public sealed class FileSystemDirectoryProbe : IDirectoryProbe
{
    /// <inheritdoc />
    public bool Exists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
}
