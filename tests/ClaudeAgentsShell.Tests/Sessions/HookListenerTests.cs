using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Hooks;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class HookListenerTests
{
    private const string TokenHeader = "X-Agents-Shell-Token";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Событие_хука_доезжает_разобранным()
    {
        await using var listener = new HookListener(TimeProvider.System);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        var status = await Send(client, listener.Endpoint, "tab-1", """
            { "hook_event_name": "SessionStart", "session_id": "9f2c0f4e", "cwd": "D:\\src\\domovoy" }
            """);

        Assert.Equal(HttpStatusCode.NoContent, status);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(HookKind.SessionStart, hook.Kind);
        Assert.Equal("9f2c0f4e", hook.SessionId);
        Assert.Equal(@"D:\src\domovoy", hook.WorkingDirectory);
        Assert.Equal("tab-1", hook.CorrelationToken);
        Assert.NotEqual(default, hook.ReceivedUtc);

        // Поля source в нагрузке не было — значит и в событии его нет. Отсутствие трактуется
        // как обычная граница хода, то есть как поведение до появления поля.
        Assert.Null(hook.Source);
    }

    [Theory]
    [InlineData("SessionStart", "compact")]
    [InlineData("SessionStart", "resume")]
    [InlineData("UserPromptSubmit", "schedule_wakeup")]
    public async Task Поле_source_доезжает_как_есть(string name, string source)
    {
        // Значения проверены по бинарнику claude.exe: у SessionStart это startup, resume,
        // clear, compact и fork; у UserPromptSubmit — кто автор промпта. Приёмник их
        // не интерпретирует и не фильтрует: решение принимает координатор состояний.
        await using var listener = new HookListener(TimeProvider.System);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", $$"""
            { "hook_event_name": "{{name}}", "session_id": "9f2c0f4e", "source": "{{source}}" }
            """);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(source, hook.Source);
    }

    [Theory]
    [InlineData("UserPromptSubmit", HookKind.UserPromptSubmit)]
    [InlineData("SubagentStart", HookKind.SubagentStart)]
    [InlineData("SubagentStop", HookKind.SubagentStop)]
    [InlineData("StopFailure", HookKind.StopFailure)]
    public async Task Хуки_промпта_сабагентов_и_оборванного_хода_разбираются(string name, HookKind expected)
    {
        // Имена точные, проверены по бинарнику claude.exe: опечатка означала бы вкладку,
        // которая молча не переключает состояние.
        await using var listener = new HookListener(TimeProvider.System);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();

        // Полезная нагрузка сабагентов несёт agent_id и agent_type, промпта — prompt,
        // StopFailure — error и error_details. Эти поля не разбираются: состояние вкладки
        // от них не зависит.
        await Send(client, listener.Endpoint, "tab-1", $$"""
            {
              "hook_event_name": "{{name}}",
              "session_id": "9f2c0f4e",
              "agent_id": "a-17",
              "agent_type": "reviewer",
              "error": "api_error",
              "error_details": "504",
              "prompt": "почини сборку"
            }
            """);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(expected, hook.Kind);
        Assert.Equal("9f2c0f4e", hook.SessionId);
    }

    [Fact]
    public async Task Слушает_только_loopback()
    {
        await using var listener = new HookListener(TimeProvider.System);
        await listener.StartAsync(CancellationToken.None);

        Assert.Equal("127.0.0.1", listener.Endpoint.Host);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(listener.Endpoint.Host)));
        Assert.NotEqual(0, listener.Endpoint.Port);
        Assert.EndsWith("/hook", listener.Endpoint.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Два_приёмника_берут_разные_свободные_порты()
    {
        await using var first = new HookListener(TimeProvider.System);
        await using var second = new HookListener(TimeProvider.System);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        Assert.NotEqual(first.Endpoint.Port, second.Endpoint.Port);
    }

    [Theory]
    [InlineData("не json вовсе")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "hook_event_name": "PreToolUse", "session_id": "9f2c" }""")]
    [InlineData("""{ "session_id": "9f2c" }""")]
    public async Task Мусор_и_незнакомый_хук_событие_не_рождают_и_приёмник_живёт(string body)
    {
        await using var listener = new HookListener(TimeProvider.System);
        var events = new ConcurrentQueue<HookEvent>();
        var valid = new TaskCompletionSource<HookEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.HookReceived += (_, args) =>
        {
            events.Enqueue(args.Event);
            if (args.Event.Kind == HookKind.Stop)
            {
                valid.TrySetResult(args.Event);
            }
        };

        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", body);

        // Приёмник обязан пережить мусор: следом отправляем корректный хук.
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "Stop" }""");
        await valid.Task.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal([HookKind.Stop], events.Select(static e => e.Kind));
    }

    [Fact]
    public async Task Пять_сессий_стучатся_одновременно_и_все_события_доезжают()
    {
        await using var listener = new HookListener(TimeProvider.System);
        var events = new ConcurrentQueue<HookEvent>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.HookReceived += (_, args) =>
        {
            events.Enqueue(args.Event);
            if (events.Count >= 5)
            {
                all.TrySetResult();
            }
        };

        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(index => Send(
            client,
            listener.Endpoint,
            "tab-" + index,
            $$"""{ "hook_event_name": "Stop", "session_id": "сессия-{{index}}" }""")));

        await all.Task.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(5, events.Count);
        Assert.Equal(
            ["tab-0", "tab-1", "tab-2", "tab-3", "tab-4"],
            events.Select(static e => e.CorrelationToken).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Токен_из_тела_подхватывается_если_заголовка_нет()
    {
        await using var listener = new HookListener(TimeProvider.System);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        using var content = new StringContent(
            """{ "hook_event_name": "SessionEnd", "correlation_token": "из-тела" }""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(listener.Endpoint, content, CancellationToken.None);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(HookKind.SessionEnd, hook.Kind);
        Assert.Equal("из-тела", hook.CorrelationToken);
    }

    [Fact]
    public async Task Адрес_до_старта_не_выдумывается()
    {
        await using var listener = new HookListener(TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => listener.Endpoint);
    }

    private static Task<HookEvent> NextEvent(IHookListener listener)
    {
        var completion = new TaskCompletionSource<HookEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.HookReceived += (_, args) => completion.TrySetResult(args.Event);
        return completion.Task;
    }

    private static async Task<HttpStatusCode> Send(HttpClient client, Uri endpoint, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await client.SendAsync(request, CancellationToken.None);
        return response.StatusCode;
    }
}
