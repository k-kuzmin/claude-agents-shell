using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Читает файлы рабочего каталога для показа человеку (<c>show_file</c>). Только чтение.
/// </summary>
public interface IWorkspaceFileReader
{
    /// <summary>
    /// Корень, от которого считаются пути: верх репозитория git, в котором лежит
    /// <paramref name="directory"/>, иначе сам каталог. <c>null</c> — каталога нет.
    /// </summary>
    Task<string?> ResolveRootAsync(string directory, CancellationToken cancellationToken);

    /// <summary>
    /// Читает один файл. Путь — относительно <paramref name="directory"/> или абсолютный;
    /// итоговый файл обязан лежать внутри <paramref name="root"/> и после разрешения ссылок.
    /// Сбой не бросает — возвращается в <see cref="ViewedFile.Problem"/>.
    /// </summary>
    /// <param name="root">Корень из <see cref="ResolveRootAsync"/>.</param>
    /// <param name="directory">Каталог, от которого считается относительный путь.</param>
    /// <param name="item">Путь и строки из аргументов агента.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Файл с путём относительно <paramref name="root"/>; фокус — как пришёл, обрезать по длине файла — дело страницы.</returns>
    Task<ViewedFile> ReadAsync(string root, string directory, ShowFileItem item, CancellationToken cancellationToken);
}
