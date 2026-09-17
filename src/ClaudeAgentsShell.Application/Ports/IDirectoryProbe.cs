namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Проверка существования каталога. Ровно один метод: прикладному коду нужно знать только это,
/// а <c>Directory.*</c> в ViewModel запрещён (раздел 8 ТЗ: исчезнувший каталог блокирует запуск).
/// </summary>
/// <remarks>
/// Метод асинхронный намеренно. Проверка сетевого пути на неотвечающем хосте упирается
/// в таймаут SMB — десятки секунд; синхронный вызов на этом месте заморозил бы окно.
/// </remarks>
public interface IDirectoryProbe
{
    /// <summary>Каталог существует и доступен.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
}
