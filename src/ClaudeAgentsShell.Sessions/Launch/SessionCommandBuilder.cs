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

    /// <summary>Аргумент, которым сессии передаётся сгенерированный файл настроек с хуками.</summary>
    private const string SettingsOption = "--settings";

    /// <inheritdoc />
    public IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch, string? hookSettingsPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launch);

        var lines = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(project.PreLaunch))
        {
            lines.Add(project.PreLaunch.Trim() + LineTerminator);
        }

        lines.Add(BuildLaunchCommand(project, launch, hookSettingsPath) + LineTerminator);
        return lines;
    }

    private static string BuildLaunchCommand(ProjectDefinition project, SessionLaunch launch, string? hookSettingsPath)
    {
        var parts = new List<string>(4) { ClaudeExecutable };

        // Пустого --settings не бывает: путь либо есть, либо сессия запускается без хуков.
        if (!string.IsNullOrWhiteSpace(hookSettingsPath))
        {
            parts.Add(SettingsOption);
            parts.Add(PowerShellArgument.Quote(hookSettingsPath));
        }

        // Иерархия SessionLaunch закрыта приватным конструктором: новых вариантов извне не бывает,
        // поэтому разбор по образцу здесь не мешает расширению.
        switch (launch)
        {
            case SessionLaunch.ResumeSession resume:
                parts.Add("--resume");
                parts.Add(PowerShellArgument.Quote(resume.SessionId));
                break;

            case SessionLaunch.ContinueLast:
                parts.Add("--continue");
                break;

            default:
                AppendExtraArgs(project, parts);
                break;
        }

        return string.Join(' ', parts);
    }

    private static void AppendExtraArgs(ProjectDefinition project, List<string> parts)
    {
        if (project.ExtraArgs is not { Count: > 0 } extraArgs)
        {
            return;
        }

        foreach (var argument in extraArgs)
        {
            if (!string.IsNullOrEmpty(argument))
            {
                parts.Add(PowerShellArgument.Quote(argument));
            }
        }
    }
}
