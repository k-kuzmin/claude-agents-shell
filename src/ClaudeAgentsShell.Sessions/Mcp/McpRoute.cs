using System.Net;
using System.Text;
using ClaudeAgentsShell.Sessions.Hooks;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Маршрут <c>/mcp</c> на приёмнике хуков: переводит HTTP в вызов <see cref="McpJsonRpcHandler"/>.
/// POST — одно сообщение JSON-RPC, ответ <c>application/json</c>; остальные методы — <c>405</c>
/// (клиент сразу после <c>initialized</c> пробует GET за потоком SSE и должен получить отказ).
/// </summary>
public sealed class McpRoute : ILoopbackRoute
{
    /// <summary>Потолок тела запроса: вызов инструмента — это несколько путей, а не мегабайты.</summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    private const int Forbidden = 403;
    private const int MethodNotAllowed = 405;
    private const int PayloadTooLarge = 413;

    private readonly McpJsonRpcHandler _handler;

    /// <inheritdoc cref="McpRoute" />
    public McpRoute(McpJsonRpcHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc />
    public string PathSegment => McpProtocol.PathSegment;

    /// <inheritdoc />
    public async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var response = context.Response;

        // Спецификация транспорта требует проверять Origin против DNS rebinding: страница в
        // браузере может постучаться на 127.0.0.1. Claude Code заголовка Origin не шлёт вовсе.
        if (!string.IsNullOrEmpty(request.Headers["Origin"]))
        {
            WriteEmpty(response, Forbidden);
            return;
        }

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            response.AddHeader("Allow", "POST");
            WriteEmpty(response, MethodNotAllowed);
            return;
        }

        var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            WriteEmpty(response, PayloadTooLarge);
            return;
        }

        var token = request.Headers[HookProtocol.TokenHeaderName];
        var reply = await _handler.HandleAsync(token, body, cancellationToken).ConfigureAwait(false);

        response.StatusCode = reply.StatusCode;
        if (reply.Body is null)
        {
            response.ContentLength64 = 0;
            return;
        }

        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = reply.Body.Length;
        await response.OutputStream.WriteAsync(reply.Body, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteEmpty(HttpListenerResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentLength64 = 0;
    }

    /// <summary>Тело запроса в UTF-8 либо <c>null</c>, если оно больше <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return null;
        }

        // Длина может быть не объявлена (chunked), поэтому потолок проверяется и по факту чтения.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        // JSON в MCP всегда UTF-8, какую бы кодировку ни объявил заголовок.
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
