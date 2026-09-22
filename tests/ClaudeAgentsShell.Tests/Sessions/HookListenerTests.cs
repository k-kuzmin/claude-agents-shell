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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
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
    [InlineData("PostToolBatch", HookKind.PostToolBatch)]
    [InlineData("PermissionRequest", HookKind.PermissionRequest)]
    public async Task Хуки_промпта_сабагентов_и_оборванного_хода_разбираются(string name, HookKind expected)
    {
        // Имена точные, проверены по бинарнику claude.exe: опечатка означала бы вкладку,
        // которая молча не переключает состояние.
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();

        // Полезная нагрузка сабагентов несёт agent_id и agent_type, промпта — prompt,
        // StopFailure — error и error_details. Из них разбирается только agent_id:
        // по нему координатор отличает хуки сабагента от хуков главного потока.
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
        Assert.Equal("a-17", hook.AgentId);
    }

    [Fact]
    public async Task Хук_главного_потока_без_agent_id_и_без_фоновых_задач_несёт_null()
    {
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "PostToolBatch" }""");

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        // Поля нет — значит неизвестно, а не «фоновых задач нет»: это разные случаи.
        Assert.Null(hook.AgentId);
        Assert.Null(hook.BackgroundTasks);
    }

    [Fact]
    public async Task Фоновые_задачи_разбираются_с_идентификатором_и_типом_а_расписания_игнорируются()
    {
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", """
            {
              "hook_event_name": "Stop",
              "background_tasks": [
                { "id": "t-1", "type": "subagent", "description": "ревью" },
                { "id": "t-2", "type": "shell" },
                {}
              ],
              "session_crons": [ { "id": "c-1", "schedule": "*/5 * * * *" } ]
            }
            """);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(
            [new BackgroundTask("t-1", "subagent"), new BackgroundTask("t-2", "shell"), new BackgroundTask(null, null)],
            hook.BackgroundTasks);
    }

    [Fact]
    public async Task Пустой_массив_фоновых_задач_это_пустой_список_а_не_null()
    {
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "Stop", "background_tasks": [] }""");

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.NotNull(hook.BackgroundTasks);
        Assert.Empty(hook.BackgroundTasks);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"subagent\"")]
    [InlineData("3")]
    [InlineData("""{ "id": "t-1", "type": "subagent" }""")]
    [InlineData("""[ { "id": "t-1", "type": "subagent" }, 42 ]""")]
    [InlineData("""[ "subagent" ]""")]
    public async Task Мусор_в_фоновых_задачах_даёт_null_а_событие_доезжает(string field)
    {
        // Мусор обнуляет всё поле, а не укорачивает список: короткий список сказал бы
        // «фоновой работы нет» при живых задачах, а null означает «неизвестно».
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", $$"""
            { "hook_event_name": "SubagentStop", "agent_id": 17, "background_tasks": {{field}} }
            """);

        var hook = await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(HookKind.SubagentStop, hook.Kind);
        Assert.Null(hook.BackgroundTasks);

        // agent_id не строкой — тоже мусор, и тоже без исключения.
        Assert.Null(hook.AgentId);
    }

    [Fact]
    public async Task Принятый_хук_попадает_в_журнал_даже_если_подписчик_упал()
    {
        var log = new RecordingHookLog();
        await using var listener = new HookListener(TimeProvider.System, log, []);
        listener.HookReceived += (_, _) => throw new InvalidOperationException("подписчик упал");
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "PermissionRequest", "agent_id": "a-3" }""");

        var hook = await log.First.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal(HookKind.PermissionRequest, hook.Kind);
        Assert.Equal("a-3", hook.AgentId);
        Assert.Equal("tab-1", hook.CorrelationToken);
    }

    [Fact]
    public async Task Мусор_в_журнал_не_попадает()
    {
        var log = new RecordingHookLog();
        await using var listener = new HookListener(TimeProvider.System, log, []);
        var received = NextEvent(listener);
        await listener.StartAsync(CancellationToken.None);

        using var client = new HttpClient();
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "PreToolUse" }""");
        await Send(client, listener.Endpoint, "tab-1", """{ "hook_event_name": "Stop" }""");
        await received.WaitAsync(Timeout, CancellationToken.None);

        Assert.Equal([HookKind.Stop], log.Events.Select(static e => e.Kind));
    }

    [Fact]
    public async Task Слушает_только_loopback()
    {
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        await listener.StartAsync(CancellationToken.None);

        Assert.Equal("127.0.0.1", listener.Endpoint.Host);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(listener.Endpoint.Host)));
        Assert.NotEqual(0, listener.Endpoint.Port);
        Assert.EndsWith("/hook", listener.Endpoint.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Два_приёмника_берут_разные_свободные_порты()
    {
        await using var first = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
        await using var second = new HookListener(TimeProvider.System, new RecordingHookLog(), []);

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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);
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
        await using var listener = new HookListener(TimeProvider.System, new RecordingHookLog(), []);

        Assert.Throws<InvalidOperationException>(() => listener.Endpoint);
    }

    private sealed class RecordingHookLog : IHookLog
    {
        private readonly TaskCompletionSource<HookEvent> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<HookEvent> Events { get; } = new();

        public Task<HookEvent> First => _first.Task;

        public void Record(HookEvent hookEvent)
        {
            Events.Enqueue(hookEvent);
            _first.TrySetResult(hookEvent);
        }
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
