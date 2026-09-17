namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Открывает каталог во внешней программе (проводник). Существует ради запрета из раздела 3
/// CLAUDE.md: <c>Process.*</c> во ViewModel недопустим.
/// </summary>
public interface IShellLauncher
{
    /// <summary>
    /// Открывает каталог в проводнике. Каталога нет или открыть не удалось — <c>false</c>,
    /// без исключения: контекстное меню не должно ронять окно.
    /// </summary>
    Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Проверка существования файла. Отдельно от <see cref="IDirectoryProbe"/>, потому что нужна
/// только диалогу настроек проекта — справочно показать, найдены ли <c>CLAUDE.md</c>
/// и <c>.mcp.json</c>. Приложение эти файлы не читает и не меняет (раздел 6.5 ТЗ).
/// </summary>
public interface IFileProbe
{
    /// <summary>Файл существует и доступен.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
}
