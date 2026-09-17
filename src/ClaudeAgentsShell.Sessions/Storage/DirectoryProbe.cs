using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Проверка каталога через <see cref="Directory.Exists(string)" />, вынесенная в пул потоков.
/// Смысл асинхронности — не заблокировать вызывающий поток: на сетевой шаре с недоступным
/// хостом проверка упирается в таймаут SMB, а вызывает её поток интерфейса.
/// </summary>
public sealed class DirectoryProbe : IDirectoryProbe
{
    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // WaitAsync, а не токен у Task.Run: токен отменил бы только постановку в очередь,
        // а вызывающий всё равно ждал бы полный таймаут SMB. Поток пула при отмене остаётся
        // занятым до возврата системного вызова — снять его нечем.
        return await Task.Run(() => Directory.Exists(path)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
