using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Mcp;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// JSON-RPC MCP-сервера <c>show_diff</c> (issue #5). Запросы — в той форме, в какой их шлёт
/// claude 2.1.280 (сняты живым прогоном с логирующим сервером).
/// </summary>
public sealed class McpJsonRpcHandlerTests
{
    private const string Token = "tab-token";

    [Fact]
    public async Task Initialize_возвращает_версию_клиента_инструменты_и_имя_сервера()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, """
            {"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{"roots":{"listChanged":true},"elicitation":{}},
             "clientInfo":{"name":"claude-code","version":"2.1.280"}},"jsonrpc":"2.0","id":0}
            """, CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var document = Parse(reply);
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());

        // id 0 — настоящий id, а не «нет id»: ответ обязан его вернуть.
        Assert.Equal(0, root.GetProperty("id").GetInt32());

        var result = root.GetProperty("result");
        Assert.Equal("2025-11-25", result.GetProperty("protocolVersion").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
        Assert.Equal("agents-shell", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("serverInfo").GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Initialized_и_прочие_уведомления_получают_202_без_тела()
    {
        var (handler, _) = Create();

        var initialized = await handler.HandleAsync(
            Token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);
        var cancelled = await handler.HandleAsync(
            Token, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""", CancellationToken.None);

        Assert.Equal(new McpReply(202, null), initialized);
        Assert.Equal(new McpReply(202, null), cancelled);
    }

    [Fact]
    public async Task Ответ_клиента_принимается_без_тела()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, """{"jsonrpc":"2.0","id":7,"result":{}}""", CancellationToken.None);

        Assert.Equal(new McpReply(202, null), reply);
    }

    /// <summary>
    /// claude 2.1.280 первым шлёт <c>server/discover</c> со строковым id и переходит к
    /// <c>initialize</c>, только получив ошибку -32601 с кодом HTTP 200 и своим id.
    /// </summary>
    [Fact]
    public async Task Server_discover_получает_32601_со_строковым_id()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, """
            {"jsonrpc":"2.0","id":"server-discover-probe-1","method":"server/discover","params":{"_meta":{}}}
            """, CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var document = Parse(reply);
        Assert.Equal("server-discover-probe-1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Ping_отвечает_пустым_результатом()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, """{"jsonrpc":"2.0","id":4,"method":"ping"}""", CancellationToken.None);

        using var document = Parse(reply);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Tools_list_описывает_show_diff_со_схемой()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, """{"method":"tools/list","jsonrpc":"2.0","id":1}""", CancellationToken.None);

        using var document = Parse(reply);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var tool = Assert.Single(tools.EnumerateArray());

        Assert.Equal("show_diff", tool.GetProperty("name").GetString());
        Assert.Contains("diff", tool.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);

        var schema = tool.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["base", "files", "note", "path"],
            properties.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("array", properties.GetProperty("files").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("files").GetProperty("items").GetProperty("type").GetString());

        // Все аргументы необязательны: вызов без них показывает всю ветку.
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public async Task Tools_call_с_верным_токеном_передаёт_аргументы_и_возвращает_текст()
    {
        var (handler, diff) = Create(new ShowDiffOutcome.Shown("Shown 2 files against origin/main."));

        var reply = await handler.HandleAsync(Token, """
            {"method":"tools/call","params":{"name":"show_diff",
             "arguments":{"base":"origin/main","path":"D:\\src\\r","files":["a.cs"," ","src/b.cs"],"note":"Рефакторинг","extra":1},
             "_meta":{"claudecode/toolUseId":"toolu_1","progressToken":2}},"jsonrpc":"2.0","id":2}
            """, CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var document = Parse(reply);
        Assert.Equal(2, document.RootElement.GetProperty("id").GetInt32());

        var result = document.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out _));
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("Shown 2 files against origin/main.", content.GetProperty("text").GetString());

        var call = Assert.Single(diff.Calls);
        Assert.Equal(Token, call.Token);
        Assert.Equal("origin/main", call.Request.BaseRef);
        Assert.Equal(@"D:\src\r", call.Request.Directory);
        Assert.Equal(["a.cs", "src/b.cs"], call.Request.Files);
        Assert.Equal("Рефакторинг", call.Request.Note);
    }

    [Fact]
    public async Task Tools_call_без_аргументов_значит_вся_ветка()
    {
        var (handler, diff) = Create();

        await handler.HandleAsync(
            Token, """{"method":"tools/call","params":{"name":"show_diff","arguments":{}},"jsonrpc":"2.0","id":2}""",
            CancellationToken.None);
        await handler.HandleAsync(
            Token, """{"method":"tools/call","params":{"name":"show_diff"},"jsonrpc":"2.0","id":3}""",
            CancellationToken.None);

        Assert.Equal(2, diff.Calls.Count);
        Assert.All(diff.Calls, static call =>
        {
            Assert.Null(call.Request.BaseRef);
            Assert.Null(call.Request.Directory);
            Assert.Null(call.Request.Note);
            Assert.Empty(call.Request.Files);
        });
    }

    /// <summary>
    /// Неверный токен — не 401 (Claude Code понял бы его как «нужна авторизация»), а ошибка
    /// инструмента: сервер жив, вкладки нет.
    /// </summary>
    [Fact]
    public async Task Tools_call_с_чужим_токеном_возвращает_isError_а_не_401()
    {
        var (handler, diff) = Create(new ShowDiffOutcome.UnknownSession());

        var reply = await handler.HandleAsync(
            "не-наш", """{"method":"tools/call","params":{"name":"show_diff","arguments":{}},"jsonrpc":"2.0","id":5}""",
            CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var document = Parse(reply);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("tab", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("не-наш", Assert.Single(diff.Calls).Token);
    }

    [Fact]
    public async Task Tools_call_без_заголовка_токена_доходит_до_обработчика_с_null()
    {
        var (handler, diff) = Create(new ShowDiffOutcome.UnknownSession());

        await handler.HandleAsync(
            null, """{"method":"tools/call","params":{"name":"show_diff"},"jsonrpc":"2.0","id":5}""",
            CancellationToken.None);

        Assert.Null(Assert.Single(diff.Calls).Token);
    }

    [Fact]
    public async Task Сбой_построения_diff_уходит_агенту_текстом_ошибки()
    {
        var (handler, _) = Create(new ShowDiffOutcome.Failed("Not a git repository: D:\\tmp"));

        var reply = await handler.HandleAsync(
            Token, """{"method":"tools/call","params":{"name":"show_diff"},"jsonrpc":"2.0","id":6}""",
            CancellationToken.None);

        using var document = Parse(reply);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(@"Not a git repository: D:\tmp", Text(result));
    }

    [Fact]
    public async Task Исключение_обработчика_не_роняет_сервер()
    {
        var (handler, _) = Create(failure: new InvalidOperationException("boom"));

        var reply = await handler.HandleAsync(
            Token, """{"method":"tools/call","params":{"name":"show_diff"},"jsonrpc":"2.0","id":6}""",
            CancellationToken.None);

        using var document = Parse(reply);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("""{"base":5}""")]
    [InlineData("""{"files":"a.cs"}""")]
    [InlineData("""{"files":["a.cs",3]}""")]
    [InlineData("""{"note":{}}""")]
    public async Task Неверный_тип_аргумента_это_ошибка_инструмента_без_вызова(string arguments)
    {
        var (handler, diff) = Create();

        var reply = await handler.HandleAsync(
            Token, $$"""{"method":"tools/call","params":{"name":"show_diff","arguments":{{arguments}}},"jsonrpc":"2.0","id":8}""",
            CancellationToken.None);

        using var document = Parse(reply);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(diff.Calls);
    }

    [Fact]
    public async Task Неизвестный_инструмент_это_ошибка_32602()
    {
        var (handler, diff) = Create();

        var reply = await handler.HandleAsync(
            Token, """{"method":"tools/call","params":{"name":"rm_rf"},"jsonrpc":"2.0","id":9}""",
            CancellationToken.None);

        using var document = Parse(reply);
        Assert.Equal(-32602, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(diff.Calls);
    }

    [Fact]
    public async Task Неизвестный_метод_это_ошибка_32601()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(
            Token, """{"jsonrpc":"2.0","id":10,"method":"resources/list"}""", CancellationToken.None);

        Assert.Equal(200, reply.StatusCode);
        using var document = Parse(reply);
        Assert.Equal(10, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task Битый_JSON_это_ошибка_32700(string body)
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(Token, body, CancellationToken.None);

        Assert.Equal(400, reply.StatusCode);
        using var document = Parse(reply);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(-32700, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Пакет_сообщений_не_поддерживается()
    {
        var (handler, _) = Create();

        var reply = await handler.HandleAsync(
            Token, """[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", CancellationToken.None);

        Assert.Equal(400, reply.StatusCode);
        using var document = Parse(reply);
        Assert.Equal(-32600, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static (McpJsonRpcHandler Handler, RecordingShowDiffHandler Diff) Create(
        ShowDiffOutcome? outcome = null,
        Exception? failure = null)
    {
        var diff = new RecordingShowDiffHandler(outcome ?? new ShowDiffOutcome.Shown("shown"), failure);
        var tool = new ShowDiffTool(diff);
        return (new McpJsonRpcHandler([tool]), diff);
    }

    private static JsonDocument Parse(McpReply reply)
    {
        Assert.NotNull(reply.Body);
        return JsonDocument.Parse(Encoding.UTF8.GetString(reply.Body));
    }

    private static string Text(JsonElement result) =>
        Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!;
}

/// <summary>Обработчик <c>show_diff</c>, который запоминает вызовы и отвечает заданным исходом.</summary>
internal sealed class RecordingShowDiffHandler(ShowDiffOutcome outcome, Exception? failure = null) : IShowDiffHandler
{
    private readonly List<(string? Token, ShowDiffRequest Request)> _calls = [];

    public IReadOnlyList<(string? Token, ShowDiffRequest Request)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ShowDiffOutcome> HandleAsync(
        string? correlationToken,
        ShowDiffRequest request,
        CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((correlationToken, request));
        }

        return failure is not null ? Task.FromException<ShowDiffOutcome>(failure) : Task.FromResult(outcome);
    }
}
