namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Каталоги приложения. Единственный источник путей: прикладной код не строит их сам
/// и не знает про <c>%APPDATA%</c>.
/// </summary>
public interface IAppDataPaths
{
    /// <summary>Каталог данных приложения (<c>%APPDATA%\ClaudeAgentsShell</c>). Создаётся при первом обращении.</summary>
    string AppData { get; }

    /// <summary>Полный путь к <c>projects.json</c>.</summary>
    string ProjectsFile { get; }

    /// <summary>
    /// Каталог с транскриптами сессий Claude Code (<c>~/.claude/projects</c>).
    /// Только для чтения: ни записи, ни удаления.
    /// </summary>
    string ClaudeProjects { get; }
}
