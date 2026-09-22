namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Готовит файл для <c>--mcp-config</c>: подключает сессии MCP-сервер приложения
/// с инструментом <c>show_diff</c> (issue #5).
/// </summary>
/// <remarks>
/// Файл генерируется **в каталоге данных приложения** и один на приложение, как настройки хуков:
/// токен вкладки подставляет сам Claude Code из окружения псевдоконсоли (<c>${ПЕРЕМЕННАЯ}</c>
/// в заголовке — проверено на claude 2.1.280). В проект пользователя не пишется ничего.
/// </remarks>
public interface IMcpConfigProvider
{
    /// <summary>
    /// Создаёт (или обновляет) файл под текущий адрес MCP-маршрута и возвращает путь к нему.
    /// </summary>
    /// <param name="endpoint">Адрес MCP-маршрута — <see cref="IHookListener.McpEndpoint"/>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="IOException">Файл создать не удалось; сессия запускается без MCP.</exception>
    Task<string> EnsureConfigFileAsync(Uri endpoint, CancellationToken cancellationToken);
}
