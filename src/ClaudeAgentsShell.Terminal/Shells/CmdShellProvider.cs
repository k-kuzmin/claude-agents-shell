using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Shells;

/// <summary>Классический <c>cmd.exe</c> — последний рубеж отката.</summary>
public sealed class CmdShellProvider : IShellProvider
{
    /// <inheritdoc />
    public ShellKind Kind => ShellKind.Cmd;

    /// <inheritdoc />
    public ShellStartCommand? TryResolve()
    {
        string? fileName = ExecutableLocator.FindFirstExisting([ExecutableLocator.SystemPath("cmd.exe")])
            ?? ExecutableLocator.FindInPath("cmd.exe");

        return fileName is null
            ? null
            : new ShellStartCommand(ShellKind.Cmd, fileName, []);
    }
}
