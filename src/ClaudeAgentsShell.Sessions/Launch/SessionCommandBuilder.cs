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

    /// <summary>
    /// Аргумент, которым сессии передаётся файл с MCP-сервером приложения. Он добавляет серверы
    /// к серверам пользователя, а не заменяет их: <c>--strict-mcp-config</c> не передаётся.
    /// </summary>
    private const string McpConfigOption = "--mcp-config";

    /// <inheritdoc />
    public IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch, SessionIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(integration);

        var lines = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(project.PreLaunch))
        {
            lines.Add(project.PreLaunch.Trim() + LineTerminator);
        }

        lines.Add(BuildLaunchCommand(project, launch, integration) + LineTerminator);
        return lines;
    }

    private static string BuildLaunchCommand(ProjectDefinition project, SessionLaunch launch, SessionIntegration integration)
    {
        var parts = new List<string>(6) { ClaudeExecutable };

        // Пустого флага не бывает: путь либо есть, либо сессия запускается без этой интеграции.
        AppendFileOption(parts, SettingsOption, integration.HookSettingsPath);
        AppendFileOption(parts, McpConfigOption, integration.McpConfigPath);

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

    private static void AppendFileOption(List<string> parts, string option, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            parts.Add(option);
            parts.Add(PowerShellArgument.Quote(path));
        }
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
