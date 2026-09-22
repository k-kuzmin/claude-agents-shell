using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Хранилище раскладки окна (<c>layout.json</c> в каталоге данных приложения, раздел 4.2 ТЗ).
/// Запись атомарная: временный файл плюс замена.
/// </summary>
public interface ILayoutStore
{
    /// <summary>
    /// Читает раскладку. Файла нет — <see cref="WorkspaceLayout.Empty"/>. Файл битый или чужой
    /// версии — тоже пустая раскладка, а сам файл откладывается в <c>.bak</c>; не исключение.
    /// </summary>
    Task<WorkspaceLayout> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Записывает раскладку целиком.</summary>
    Task SaveAsync(WorkspaceLayout layout, CancellationToken cancellationToken);
}
