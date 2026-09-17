using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Проверка файла через <see cref="File.Exists(string)" />, вынесенная в пул потоков —
/// по той же причине, что и <see cref="DirectoryProbe" />: на недоступной сетевой шаре
/// проверка упирается в таймаут SMB, а зовут её из потока интерфейса.
/// </summary>
public sealed class FileProbe : IFileProbe
{
    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return await Task.Run(() => File.Exists(path)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
