using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Какие оболочки действительно установлены. Нужен диалогу настроек проекта, чтобы
/// показать пользователю правду: раздел 8 ТЗ требует отражать в настройках откат
/// с отсутствующей оболочки.
/// </summary>
/// <remarks>
/// Отдельный порт, а не <see cref="Application.Ports.IShellResolver"/> целиком: диалогу
/// нужен только список, а запускать оболочку он не вправе (ISP, раздел 3 CLAUDE.md).
/// Метод асинхронный намеренно — поиск по <c>PATH</c> и проверка файлов ходят на диск,
/// и в потоке интерфейса им делать нечего.
/// </remarks>
public interface IShellAvailability
{
    /// <summary>Оболочки, найденные в системе, в порядке предпочтения резолвера.</summary>
    /// <remarks>
    /// Порядок значим: первый элемент — то, во что превратится запуск, если выбранной
    /// оболочки нет. Пустой список означает, что запустить сессию не получится ничем.
    /// </remarks>
    Task<IReadOnlyList<ShellKind>> GetInstalledAsync(CancellationToken cancellationToken);
}
