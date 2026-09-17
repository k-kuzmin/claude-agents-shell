using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
namespace ClaudeAgentsShell.Sessions.Launch;

/// <summary>
/// Строки stdin для запуска сессии (раздел 5.1 ТЗ): сначала <c>preLaunch</c>, затем <c>claude</c>.
/// Ввода-вывода нет — режимы запуска и экранирование проверяются без живого PTY.
/// </summary>
public sealed class SessionCommandBuilder : ISessionCommandBuilder
{
    /// <summary>
    /// Завершитель строки. В stdin PTY идёт <c>CR</c> — ровно то, что шлёт xterm.js по Enter;
    /// <c>LF</c> оболочка за нажатие Enter не считает и команда осталась бы невыполненной.
    /// </summary>
    private const string LineTerminator = "\r";

    private const string ClaudeExecutable = "claude";

    /// <inheritdoc />
    public IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launch);

        var lines = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(project.PreLaunch))
        {
            lines.Add(project.PreLaunch.Trim() + LineTerminator);
        }

        lines.Add(BuildLaunchCommand(project, launch) + LineTerminator);
        return lines;
    }

    private static string BuildLaunchCommand(ProjectDefinition project, SessionLaunch launch) => launch switch
    {
        // Иерархия SessionLaunch закрыта приватным конструктором: новых вариантов извне не бывает,
        // поэтому разбор по образцу здесь не мешает расширению.
        SessionLaunch.ResumeSession resume => $"{ClaudeExecutable} --resume {PowerShellArgument.Quote(resume.SessionId)}",
        SessionLaunch.ContinueLast => $"{ClaudeExecutable} --continue",
        _ => NewSessionCommand(project),
    };

    private static string NewSessionCommand(ProjectDefinition project)
    {
        if (project.ExtraArgs is not { Count: > 0 } extraArgs)
        {
            return ClaudeExecutable;
        }

        var parts = new List<string>(extraArgs.Count + 1) { ClaudeExecutable };
        foreach (var argument in extraArgs)
        {
            if (!string.IsNullOrEmpty(argument))
            {
                parts.Add(PowerShellArgument.Quote(argument));
            }
        }

        return string.Join(' ', parts);
    }
}
