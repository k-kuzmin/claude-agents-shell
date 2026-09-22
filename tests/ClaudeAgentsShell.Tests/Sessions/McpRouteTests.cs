using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Hooks;
using ClaudeAgentsShell.Sessions.Mcp;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Маршрут <c>/mcp</c> на настоящем <see cref="HookListener"/>: тот же порт, что у хуков,
/// транспорт Streamable HTTP без SSE.
/// </summary>
public sealed class McpRouteTests
{
    private const string TokenHeader = "X-Agents-Shell-Token";

    [Fact]
    public async Task Адрес_MCP_на_том_же_порту_что_и_хуки()
    {
        await using var listener = CreateListener(out _);
        await listener.StartAsync(CancellationToken.None);

        Assert.Equal("/mcp", listener.McpEndpoint.AbsolutePath);
        Assert.Equal(listener.Endpoint.Port, listener.McpEndpoint.Port);
        Assert.Equal("127.0.0.1", listener.McpEndpoint.Host);
    }

    [Fact]
    public async Task Без_маршрута_адреса_MCP_нет()
    {
        await using var listener = new HookListener(TimeProvider.System, new NullHookLog(), []);
        await listener.StartAsync(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => listener.McpEndpoint);
    }

    [Fact]
    public async Task До_старта_адреса_MCP_нет()
    {
        await using var listener = CreateListener(out _);

        Assert.Throws<InvalidOperationException>(() => listener.McpEndpoint);
    }

    [Fact]
    public async Task Initialize_по_HTTP_отвечает_application_json()
    {
        await using var listener = CreateListener(out _);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var response = await Post(client, listener.McpEndpoint, "tab-1", """
            {"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{}},"jsonrpc":"2.0","id":0}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("agents-shell", document.RootElement.GetProperty("result").GetProperty("serverInfo")
            .GetProperty("name").GetString());
    }

    [Fact]
    public async Task Уведомление_по_HTTP_получает_202_без_тела()
    {
        await using var listener = CreateListener(out _);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var response = await Post(client, listener.McpEndpoint, "tab-1",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Токен_вкладки_из_заголовка_доезжает_до_обработчика()
    {
        await using var listener = CreateListener(out var diff);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var response = await Post(client, listener.McpEndpoint, "tab-42", """
            {"method":"tools/call","params":{"name":"show_diff","arguments":{"files":["Сцена.unity"]}},"jsonrpc":"2.0","id":2}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(diff.Calls);
        Assert.Equal("tab-42", call.Token);
        Assert.Equal(["Сцена.unity"], call.Request.Files);
    }

    /// <summary>Claude Code сразу после <c>initialized</c> пробует GET за потоком SSE.</summary>
    [Fact]
    public async Task GET_получает_405()
    {
        await using var listener = CreateListener(out _);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, listener.McpEndpoint);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Запрос_из_браузера_с_Origin_отклоняется()
    {
        await using var listener = CreateListener(out var diff);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, listener.McpEndpoint)
        {
            Content = new StringContent(
                """{"method":"tools/call","params":{"name":"show_diff"},"jsonrpc":"2.0","id":1}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(diff.Calls);
    }

    [Fact]
    public async Task Слишком_большое_тело_отклоняется()
    {
        await using var listener = CreateListener(out var diff);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var response = await Post(client, listener.McpEndpoint, "tab-1", new string(' ', 2 * 1024 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(diff.Calls);
    }

    [Fact]
    public async Task Хуки_работают_рядом_с_MCP()
    {
        await using var listener = CreateListener(out _);
        var received = new TaskCompletionSource<HookEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.HookReceived += (_, args) => received.TrySetResult(args.Event);
        await listener.StartAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var response = await Post(client, listener.Endpoint, "tab-1",
            """{ "hook_event_name": "Stop", "session_id": "s1" }""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var hook = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HookKind.Stop, hook.Kind);
    }

    [Fact]
    public void Занять_путь_хуков_маршрутом_нельзя()
    {
        Assert.Throws<ArgumentException>(
            () => new HookListener(TimeProvider.System, new NullHookLog(), [new FixedRoute("hook")]));
        Assert.Throws<ArgumentException>(
            () => new HookListener(TimeProvider.System, new NullHookLog(), [new FixedRoute("x"), new FixedRoute("X")]));
    }

    private static HookListener CreateListener(out RecordingShowDiffHandler diff)
    {
        var recording = new RecordingShowDiffHandler(new ShowDiffOutcome.Shown("shown"));
        diff = recording;
        var handler = new McpJsonRpcHandler([new ShowDiffTool(new Lazy<IShowDiffHandler>(() => recording))]);
        return new HookListener(TimeProvider.System, new NullHookLog(), [new McpRoute(handler)]);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, Uri endpoint, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(TokenHeader, token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, CancellationToken.None);
    }

    private sealed class NullHookLog : IHookLog
    {
        public ConcurrentQueue<HookEvent> Events { get; } = new();

        public void Record(HookEvent hookEvent) => Events.Enqueue(hookEvent);
    }

    private sealed class FixedRoute(string segment) : ILoopbackRoute
    {
        public string PathSegment => segment;

        public Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
