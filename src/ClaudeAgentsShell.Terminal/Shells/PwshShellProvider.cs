using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Shells;

/// <summary>
/// PowerShell 7+ (<c>pwsh.exe</c>). Ищется не только в <c>PATH</c>: установщик MSI кладёт
/// оболочку в <c>%ProgramFiles%\PowerShell\7</c>, а из магазина она попадает в
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> и в <c>PATH</c> может не попасть вовсе.
/// </summary>
public sealed class PwshShellProvider : IShellProvider
{
    /// <inheritdoc />
    public ShellKind Kind => ShellKind.Pwsh;

    /// <inheritdoc />
    public ShellStartCommand? TryResolve()
    {
        string? fileName = ExecutableLocator.FindFirstExisting(EnumerateCandidates())
            ?? ExecutableLocator.FindInPath("pwsh.exe");

        return fileName is null
            ? null
            : new ShellStartCommand(ShellKind.Pwsh, fileName, ["-NoLogo"]);
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (string root in EnumerateProgramFilesRoots())
        {
            string powerShell = Path.Combine(root, "PowerShell");
            if (!Directory.Exists(powerShell))
            {
                continue;
            }

            // Сначала конкретные мажорные версии от новых к старым, затем «preview» и прочее.
            IEnumerable<string> versionDirectories;
            try
            {
                versionDirectories = Directory.EnumerateDirectories(powerShell)
                    .OrderByDescending(static d => d, StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string versionDirectory in versionDirectories)
            {
                yield return Path.Combine(versionDirectory, "pwsh.exe");
            }
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "pwsh.exe");
    }

    private static IEnumerable<string> EnumerateProgramFilesRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }
}
