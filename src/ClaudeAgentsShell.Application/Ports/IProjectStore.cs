using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Хранилище списка проектов (<c>projects.json</c> в каталоге данных приложения).
/// Запись атомарная: временный файл плюс замена.
/// </summary>
public interface IProjectStore
{
    /// <summary>Читает список проектов. Файла нет или он битый — пустой список, не исключение.</summary>
    Task<IReadOnlyList<ProjectDefinition>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Записывает список целиком.</summary>
    Task SaveAsync(IReadOnlyList<ProjectDefinition> projects, CancellationToken cancellationToken);
}
