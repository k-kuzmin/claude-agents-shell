using System.Text.Json;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>Результат вызова инструмента — уходит агенту одним текстовым блоком.</summary>
/// <param name="Text">Текст результата.</param>
/// <param name="IsError">Ошибка инструмента: агент увидит её как неудавшийся вызов.</param>
public sealed record McpToolResult(string Text, bool IsError)
{
    /// <summary>Успешный результат.</summary>
    public static McpToolResult Success(string text) => new(text, false);

    /// <summary>Ошибка инструмента.</summary>
    public static McpToolResult Error(string text) => new(text, true);
}

/// <summary>
/// Инструмент MCP-сервера приложения. Новый инструмент добавляется реализацией,
/// а не веткой в <see cref="McpJsonRpcHandler"/>.
/// </summary>
public interface IMcpTool
{
    /// <summary>Имя инструмента в <c>tools/list</c> и <c>tools/call</c>.</summary>
    string Name { get; }

    /// <summary>Описание для агента: что делает и когда вызывать.</summary>
    string Description { get; }

    /// <summary>JSON Schema аргументов (<c>inputSchema</c>).</summary>
    JsonElement InputSchema { get; }

    /// <summary>Выполняет вызов.</summary>
    /// <param name="correlationToken">Токен вкладки из заголовка запроса; <c>null</c> — заголовка не было.</param>
    /// <param name="arguments">Аргументы вызова — объект либо <c>null</c>, если их не прислали.</param>
    /// <param name="cancellationToken">Остановка приёмника.</param>
    Task<McpToolResult> CallAsync(string? correlationToken, JsonElement? arguments, CancellationToken cancellationToken);
}
