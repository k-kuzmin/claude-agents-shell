using System.ComponentModel;
using System.Diagnostics;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Launch;

/// <summary>
/// Открывает каталог в проводнике. Запуск процесса живёт здесь, а не во ViewModel:
/// <c>Process.*</c> в ней запрещён разделом 3 CLAUDE.md.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    private const string Explorer = "explorer.exe";

    /// <inheritdoc />
    public async Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // И проверка каталога, и запуск процесса блокируют поток: на сетевом пути это надолго.
        return await Task.Run(() => TryOpen(path)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryOpen(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo(Explorer, $"\"{path}\"")
            {
                UseShellExecute = true,
            });

            // Проводник может вернуть уже завершившийся процесс — это не признак неудачи.
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException
                                              or PlatformNotSupportedException)
        {
            // Контекстное меню не должно ронять окно: не открылось — значит false.
            return false;
        }
    }
}
