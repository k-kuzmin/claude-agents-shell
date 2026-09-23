using System.Buffers;
using System.Text.Json;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>Ответ на POST к MCP-маршруту: код HTTP и тело JSON, если оно есть.</summary>
/// <param name="StatusCode">Код ответа HTTP.</param>
/// <param name="Body">Тело в UTF-8; <c>null</c> — ответ без тела (<c>202</c> на уведомление).</param>
public sealed record McpReply(int StatusCode, byte[]? Body);

/// <summary>
/// Минимальный MCP-сервер поверх JSON-RPC 2.0 для транспорта Streamable HTTP без SSE: одно
/// сообщение в POST — один ответ <c>application/json</c>. Сессий (<c>Mcp-Session-Id</c>) нет.
/// <para>
/// Методы: <c>initialize</c>, <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>. Уведомления
/// и ответы клиента — <c>202</c> без тела. Незнакомый метод с <c>id</c> — ошибка <c>-32601</c>
/// с кодом HTTP 200: claude 2.1.280 первым шлёт <c>server/discover</c> со строковым <c>id</c>
/// и переходит к <c>initialize</c>, только получив такой ответ.
/// </para>
/// <para>
/// Неверный токен не превращается в 401: Claude Code понял бы его как «нужна авторизация».
/// Сервер отвечает, а ошибку вернёт сам инструмент (<c>isError</c>).
/// </para>
/// </summary>
public sealed class McpJsonRpcHandler
{
    private const int Ok = 200;
    private const int Accepted = 202;
    private const int BadRequest = 400;

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    private static readonly string ServerVersion =
        typeof(McpJsonRpcHandler).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly IReadOnlyList<IMcpTool> _tools;

    /// <inheritdoc cref="McpJsonRpcHandler" />
    public McpJsonRpcHandler(IEnumerable<IMcpTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = [.. tools];
    }

    /// <summary>Обрабатывает тело одного POST.</summary>
    /// <param name="correlationToken">Токен вкладки из заголовка; <c>null</c> — заголовка не было.</param>
    /// <param name="body">Тело запроса.</param>
    /// <param name="cancellationToken">Остановка приёмника.</param>
    public async Task<McpReply> HandleAsync(string? correlationToken, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Error(BadRequest, id: null, ParseError, "Parse error");
        }

        using (document)
        {
            var message = document.RootElement;

            // Пакеты сообщений (массив) из протокола убраны; поддерживается одно сообщение.
            if (message.ValueKind != JsonValueKind.Object)
            {
                return Error(BadRequest, id: null, InvalidRequest, "Invalid Request");
            }

            // Наличие id проверяется по свойству, а не по значению: initialize приходит с id 0.
            var hasId = message.TryGetProperty("id", out var id);

            if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                // Ответ клиента на наш запрос (у нас их не бывает) или уведомление без метода — принять и забыть.
                return hasId && !message.TryGetProperty("result", out _) && !message.TryGetProperty("error", out _)
                    ? Error(BadRequest, id, InvalidRequest, "Invalid Request")
                    : new McpReply(Accepted, null);
            }

            if (!hasId)
            {
                // notifications/initialized, notifications/cancelled и прочие: ответа не бывает.
                return new McpReply(Accepted, null);
            }

            message.TryGetProperty("params", out var parameters);

            return methodElement.GetString() switch
            {
                "initialize" => Result(id, writer => WriteInitialize(writer, parameters)),
                "ping" => Result(id, WriteEmptyResult),
                "tools/list" => Result(id, WriteToolsList),
                "tools/call" => await CallToolAsync(correlationToken, id, parameters, cancellationToken).ConfigureAwait(false),
                _ => Error(Ok, id, MethodNotFound, "Method not found"),
            };
        }
    }

    private async Task<McpReply> CallToolAsync(
        string? correlationToken,
        JsonElement id,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return Error(Ok, id, InvalidParams, "Invalid params: tool name is required");
        }

        var name = nameElement.GetString();
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            return Error(Ok, id, InvalidParams, $"Unknown tool: {name}");
        }

        JsonElement? arguments = parameters.TryGetProperty("arguments", out var args) ? args : null;
        var result = await tool.CallAsync(correlationToken, arguments, cancellationToken).ConfigureAwait(false);

        return Result(id, writer =>
        {
            writer.WriteStartObject("result");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", result.Text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (result.IsError)
            {
                writer.WriteBoolean("isError", true);
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteInitialize(Utf8JsonWriter writer, JsonElement parameters)
    {
        // Версия клиента возвращается как есть: из протокола сервер использует только то,
        // что не менялось между версиями.
        var version = parameters.ValueKind == JsonValueKind.Object
                      && parameters.TryGetProperty("protocolVersion", out var requested)
                      && requested.ValueKind == JsonValueKind.String
                      && !string.IsNullOrEmpty(requested.GetString())
            ? requested.GetString()!
            : McpProtocol.FallbackProtocolVersion;

        writer.WriteStartObject("result");
        writer.WriteString("protocolVersion", version);
        writer.WriteStartObject("capabilities");
        writer.WriteStartObject("tools");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("serverInfo");
        writer.WriteString("name", McpProtocol.ServerName);
        writer.WriteString("version", ServerVersion);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteEmptyResult(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("result");
        writer.WriteEndObject();
    }

    private void WriteToolsList(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("result");
        writer.WriteStartArray("tools");
        foreach (var tool in _tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            tool.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static McpReply Result(JsonElement id, Action<Utf8JsonWriter> writeResult) =>
        new(Ok, Envelope(id, writeResult));

    private static McpReply Error(int statusCode, JsonElement? id, int code, string text) =>
        new(statusCode, Envelope(id, writer =>
        {
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", text);
            writer.WriteEndObject();
        }));

    /// <summary>Конверт JSON-RPC: <c>id</c> возвращается как пришёл — числом или строкой.</summary>
    private static byte[] Envelope(JsonElement? id, Action<Utf8JsonWriter> writeMember)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is { } value)
            {
                value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writeMember(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
