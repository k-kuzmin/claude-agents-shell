using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Shells;

/// <summary>Windows PowerShell 5.1 — откат, когда <c>pwsh</c> не установлен.</summary>
public sealed class WindowsPowerShellProvider : IShellProvider
{
    /// <inheritdoc />
    public ShellKind Kind => ShellKind.WindowsPowerShell;

    /// <inheritdoc />
    public ShellStartCommand? TryResolve()
    {
        string? fileName = ExecutableLocator.FindFirstExisting(
        [
            ExecutableLocator.SystemPath("WindowsPowerShell", "v1.0", "powershell.exe"),
        ]) ?? ExecutableLocator.FindInPath("powershell.exe");

        return fileName is null
            ? null
            : new ShellStartCommand(ShellKind.WindowsPowerShell, fileName, ["-NoLogo"]);
    }
}
