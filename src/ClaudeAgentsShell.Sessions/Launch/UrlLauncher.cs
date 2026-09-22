using System.ComponentModel;
using System.Diagnostics;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Launch;

/// <summary>
/// Открывает ссылку в браузере по умолчанию через оболочку Windows. Запуск процесса живёт
/// здесь, а не во ViewModel: <c>Process.*</c> в ней запрещён разделом 3 CLAUDE.md.
/// </summary>
public sealed class UrlLauncher : IUrlLauncher
{
    /// <inheritdoc />
    public async Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryNormalize(url, out string? absolute))
        {
            return false;
        }

        // Оболочка ищет обработчик схемы и поднимает браузер — это надолго блокирует поток.
        return await Task.Run(() => TryOpen(absolute)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Пропускает только абсолютные <c>http</c> и <c>https</c>. Без этой проверки
    /// <c>UseShellExecute</c> запустил бы что угодно: и файл с диска, и произвольную схему,
    /// зарегистрированную в системе.
    /// </summary>
    private static bool TryNormalize(string url, out string absolute)
    {
        absolute = string.Empty;

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return false;
        }

        absolute = uri.AbsoluteUri;
        return true;
    }

    private static bool TryOpen(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });

            // Браузер мог уже работать и открыть вкладку в существующем процессе —
            // возвращённый null признаком неудачи не является.
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException
                                              or PlatformNotSupportedException)
        {
            // Браузера нет или оболочка отказала: окно с сообщением об ошибке не должно
            // падать из-за того, что не смогло открыть ссылку.
            return false;
        }
    }
}
