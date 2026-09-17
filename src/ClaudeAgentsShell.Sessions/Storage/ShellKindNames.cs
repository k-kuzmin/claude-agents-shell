using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Имена оболочек в <c>projects.json</c>: в ТЗ это <c>"pwsh"</c>, а не имя элемента
/// перечисления. Отсюда отдельная таблица вместо <c>JsonStringEnumConverter</c>.
/// </summary>
internal static class ShellKindNames
{
    public const string Pwsh = "pwsh";
    public const string WindowsPowerShell = "powershell";
    public const string Cmd = "cmd";

    /// <summary>Имя для записи в файл.</summary>
    public static string ToName(ShellKind kind) => kind switch
    {
        ShellKind.WindowsPowerShell => WindowsPowerShell,
        ShellKind.Cmd => Cmd,
        _ => Pwsh,
    };

    /// <summary>
    /// Разбор имени из файла. Неизвестное или отсутствующее значение — это
    /// <see cref="ShellKind.Pwsh" />: предпочтительный вариант по разделу 2 ТЗ.
    /// </summary>
    public static ShellKind FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShellKind.Pwsh;
        }

        var trimmed = name.Trim();

        if (string.Equals(trimmed, WindowsPowerShell, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, nameof(ShellKind.WindowsPowerShell), StringComparison.OrdinalIgnoreCase))
        {
            return ShellKind.WindowsPowerShell;
        }

        if (string.Equals(trimmed, Cmd, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ShellKind.Cmd;
        }

        return ShellKind.Pwsh;
    }
}
