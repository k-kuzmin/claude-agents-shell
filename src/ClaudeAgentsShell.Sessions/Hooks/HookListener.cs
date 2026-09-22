using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Mcp;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Локальный приёмник хуков на <see cref="HttpListener" />. Слушает только loopback:
/// <c>http://127.0.0.1:&lt;порт&gt;/hook/</c>, порт выбирается свободный при старте.
/// <para>
/// На том же порту живут прочие маршруты (<see cref="ILoopbackRoute"/>, например <c>/mcp</c>):
/// приёмник только принимает соединение и отдаёт его маршруту по первому сегменту пути.
/// </para>
/// <para>
/// Ответ отдаётся коротким и до того, как поднимается <see cref="HookReceived" />:
/// медленный подписчик не должен задерживать <c>claude</c>, который ждёт завершения хука.
/// Событие приходит из потока пула — подписчик сам переводит его в свой диспетчер.
/// </para>
/// </summary>
public sealed class HookListener : IHookListener
{
    private const int PortAttempts = 5;
    private const int NoContent = 204;
    private const int NotFound = 404;
    private const int InternalServerError = 500;

    private readonly TimeProvider _timeProvider;
    private readonly IHookLog _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, ILoopbackRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    private Task? _acceptLoop;
    private Uri? _endpoint;
    private int _port;
    private bool _disposed;

    /// <inheritdoc cref="HookListener" />
    /// <param name="timeProvider">Часы для метки приёма хука.</param>
    /// <param name="log">Журнал принятых хуков.</param>
    /// <param name="routes">Прочие маршруты на том же порту; путь <c>hook</c> занят самим приёмником.</param>
    public HookListener(TimeProvider timeProvider, IHookLog log, IEnumerable<ILoopbackRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(routes);
        _timeProvider = timeProvider;
        _log = log;

        foreach (var route in routes)
        {
            if (string.Equals(route.PathSegment, HookProtocol.PathSegment, StringComparison.OrdinalIgnoreCase)
                || !_routes.TryAdd(route.PathSegment, route))
            {
                throw new ArgumentException($"Путь /{route.PathSegment} уже занят.", nameof(routes));
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<HookEventArgs>? HookReceived;

    /// <inheritdoc />
    public Uri Endpoint => _endpoint
        ?? throw new InvalidOperationException("Приёмник хуков ещё не запущен: адрес неизвестен.");

    /// <inheritdoc />
    public Uri McpEndpoint
    {
        get
        {
            _ = Endpoint;
            return _routes.ContainsKey(McpProtocol.PathSegment)
                ? new Uri($"http://127.0.0.1:{_port}/{McpProtocol.PathSegment}")
                : throw new InvalidOperationException("MCP-маршрут не зарегистрирован: адреса нет.");
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_endpoint is not null)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        HttpListenerException? last = null;
        for (var attempt = 0; attempt < PortAttempts && _endpoint is null; attempt++)
        {
            var port = FindFreePort();

            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/{HookProtocol.PathSegment}/");
            foreach (var segment in _routes.Keys)
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/{segment}/");
            }

            try
            {
                _listener.Start();
                _port = port;
                _endpoint = new Uri($"http://127.0.0.1:{port}/{HookProtocol.PathSegment}");
            }
            catch (HttpListenerException exception)
            {
                // Порт успели занять между освобождением пробного сокета и стартом слушателя.
                last = exception;
            }
        }

        if (_endpoint is null)
        {
            throw last ?? new HttpListenerException();
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _cancellation.CancelAsync().ConfigureAwait(false);

        // Close, а не Stop: он же разблокирует висящий GetContextAsync.
        _listener.Close();

        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановка по запросу — штатное завершение цикла приёма.
            }
        }

        _cancellation.Dispose();
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException
                                                  or ObjectDisposedException
                                                  or InvalidOperationException)
            {
                // Слушателя закрыли — это выход из цикла, а не сбой.
                return;
            }

            // Приём следующего хука не ждёт разбора текущего: пять сессий стучатся одновременно.
            _ = DispatchAsync(context, cancellationToken);
        }
    }

    private Task DispatchAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var segment = FirstSegment(context.Request.Url);

        if (string.Equals(segment, HookProtocol.PathSegment, StringComparison.OrdinalIgnoreCase))
        {
            return HandleHookAsync(context, cancellationToken);
        }

        if (_routes.TryGetValue(segment, out var route))
        {
            return HandleRouteAsync(route, context, cancellationToken);
        }

        Respond(context.Response, NotFound);
        return Task.CompletedTask;
    }

    private static string FirstSegment(Uri? url)
    {
        var path = (url?.AbsolutePath ?? string.Empty).AsSpan().TrimStart('/');
        var slash = path.IndexOf('/');
        return (slash < 0 ? path : path[..slash]).ToString();
    }

    private static async Task HandleRouteAsync(
        ILoopbackRoute route,
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await route.HandleAsync(context, cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        catch (Exception exception) when (exception is IOException
                                              or HttpListenerException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // Клиент ушёл или приёмник останавливается — отвечать некому.
            completed = true;
        }
        catch (Exception)
        {
            // Сбой маршрута не должен теряться в необслуживаемой задаче молча для клиента:
            // он получит 500, а цикл приёма продолжит работу.
        }
        finally
        {
            if (completed)
            {
                CloseQuietly(context.Response);
            }
            else
            {
                Respond(context.Response, InternalServerError);
            }
        }
    }

    private static void Respond(HttpListenerResponse response, int statusCode)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentLength64 = 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or HttpListenerException
                                              or ObjectDisposedException)
        {
            // Заголовки уже ушли — код не поменять, остаётся закрыть.
        }

        CloseQuietly(response);
    }

    private static void CloseQuietly(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch (Exception exception) when (exception is IOException
                                              or HttpListenerException
                                              or ObjectDisposedException)
        {
            // Клиент уже ушёл — закрывать нечего.
        }
    }

    private async Task HandleHookAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        string? body = null;
        string? token = null;

        try
        {
            token = context.Request.Headers[HookProtocol.TokenHeaderName];

            using (var reader = new StreamReader(
                context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            context.Response.StatusCode = NoContent;
            context.Response.ContentLength64 = 0;
        }
        catch (Exception exception) when (exception is IOException
                                              or HttpListenerException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // Обрыв соединения на середине запроса: тела нет, событие не родится, цикл живёт дальше.
            body = null;
        }
        finally
        {
            CloseQuietly(context.Response);
        }

        var hookEvent = HookPayload.TryParse(body, token, _timeProvider.GetUtcNow());
        if (hookEvent is not null && !cancellationToken.IsCancellationRequested)
        {
            // Журнал — до подписчиков: упавший подписчик не должен стоить строки о хуке,
            // а сам журнал диска не ждёт и не бросает.
            _log.Record(hookEvent);
            HookReceived?.Invoke(this, new HookEventArgs(hookEvent));
        }
    }
}
