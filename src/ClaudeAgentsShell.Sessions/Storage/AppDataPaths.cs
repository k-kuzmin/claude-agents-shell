using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Пути приложения. Корни передаются в конструктор, поэтому тесты работают во временном
/// каталоге и ничего не создают в настоящем профиле пользователя.
/// </summary>
public sealed class AppDataPaths : IAppDataPaths
{
    /// <summary>Имя файла со списком проектов (раздел 4.1 ТЗ).</summary>
    public const string ProjectsFileName = "projects.json";

    /// <summary>Имя файла раскладки окна (issue #4).</summary>
    public const string LayoutFileName = "layout.json";

    private readonly string _appData;
    private volatile bool _created;

    /// <param name="appDataDirectory">Каталог данных приложения; создаётся при первом обращении.</param>
    /// <param name="claudeProjectsDirectory">Каталог транскриптов Claude Code; только для чтения.</param>
    public AppDataPaths(string appDataDirectory, string claudeProjectsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeProjectsDirectory);

        _appData = Path.GetFullPath(appDataDirectory);
        ClaudeProjects = Path.GetFullPath(claudeProjectsDirectory);
    }

    /// <summary>Пути текущего пользователя: <c>%APPDATA%\ClaudeAgentsShell</c> и <c>~/.claude/projects</c>.</summary>
    public static AppDataPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeAgentsShell"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));

    /// <inheritdoc />
    public string AppData
    {
        get
        {
            EnsureAppDataExists();
            return _appData;
        }
    }

    /// <inheritdoc />
    public string ProjectsFile => Path.Combine(AppData, ProjectsFileName);

    /// <inheritdoc />
    public string LayoutFile => Path.Combine(AppData, LayoutFileName);

    /// <summary>
    /// Каталог транскриптов Claude Code. Геттер сознательно ничего не создаёт: раздел 7
    /// CLAUDE.md запрещает любую запись в <c>~/.claude/projects</c>.
    /// </summary>
    public string ClaudeProjects { get; }

    private void EnsureAppDataExists()
    {
        if (_created)
        {
            return;
        }

        Directory.CreateDirectory(_appData);
        _created = true;
    }
}
