namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Имена MCP-интеграции в одном экземпляре: сервер, инструмент, путь маршрута и правило
/// разрешения должны совпадать между <c>mcp.json</c>, <c>hook-settings.json</c> и маршрутом —
/// расхождение не ломает сборку, а тихо оставляет агента без инструмента или с вопросом
/// о разрешении на каждый вызов.
/// </summary>
internal static class McpProtocol
{
    /// <summary>
    /// Имя сервера в <c>mcpServers</c>. Уникальное: <c>--mcp-config</c> добавляет серверы к серверам
    /// пользователя, и одноимённый сервер пользователя был бы подменён на время сеанса.
    /// </summary>
    public const string ServerName = "agents-shell";

    /// <summary>Имя инструмента.</summary>
    public const string ShowDiffToolName = "show_diff";

    /// <summary>Правило <c>permissions.allow</c>: так Claude Code называет инструмент MCP-сервера.</summary>
    public const string ShowDiffPermissionRule = "mcp__" + ServerName + "__" + ShowDiffToolName;

    /// <summary>Путь маршрута: <c>http://127.0.0.1:&lt;порт&gt;/mcp</c>.</summary>
    public const string PathSegment = "mcp";

    /// <summary>
    /// Версия протокола на случай, если клиент не прислал свою в <c>initialize</c>. Обычно
    /// возвращается версия клиента: сервер поддерживает только общее подмножество методов.
    /// </summary>
    public const string FallbackProtocolVersion = "2025-11-25";
}
