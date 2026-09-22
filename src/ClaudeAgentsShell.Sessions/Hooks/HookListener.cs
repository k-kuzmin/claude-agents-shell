using System.Net;
using System.Net.Sockets;
using System.Text;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Локальный приёмник хуков на <see cref="HttpListener" />. Слушает только loopback:
/// <c>http://127.0.0.1:&lt;порт&gt;/hook/</c>, порт выбирается свободный при старте.
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

    private readonly TimeProvider _timeProvider;
    private readonly IHookLog _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _acceptLoop;
    private Uri? _endpoint;
    private bool _disposed;

    /// <inheritdoc cref="HookListener" />
    public HookListener(TimeProvider timeProvider, IHookLog log)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);
        _timeProvider = timeProvider;
        _log = log;
    }

    /// <inheritdoc />
    public event EventHandler<HookEventArgs>? HookReceived;

    /// <inheritdoc />
    public Uri Endpoint => _endpoint
        ?? throw new InvalidOperationException("Приёмник хуков ещё не запущен: адрес неизвестен.");

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

            try
            {
                _listener.Start();
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
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
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
            try
            {
                context.Response.Close();
            }
            catch (Exception exception) when (exception is IOException
                                                  or HttpListenerException
                                                  or ObjectDisposedException)
            {
                // Клиент уже ушёл — закрывать нечего.
            }
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
