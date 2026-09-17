using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Protocol;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Единственное место в приложении, которое знает про WebView2. Ровно один контрол на окно:
/// страница держит N экземпляров xterm.js, переключение вкладки — смена видимости контейнера.
/// Ассеты страницы отдаются через <c>SetVirtualHostNameToFolderMapping</c>; CDN не используется.
/// </summary>
public sealed class WebView2TerminalBridge : ITerminalBridge
{
    private const string VirtualHost = "app.local";
    private const string PageUrl = "https://app.local/index.html";

    /// <summary>
    /// Ставится до навигации и переживает создание документа: сообщения, посланные из C#
    /// раньше, чем страница успела подписаться, копятся в очереди, а не теряются.
    /// </summary>
    private const string InboxScript = """
        (function () {
            if (window.__shellInbox) { return; }
            window.__shellInbox = [];
            window.chrome.webview.addEventListener('message', function (event) {
                var handler = window.__shellOnMessage;
                if (handler) { handler(event.data); } else { window.__shellInbox.push(event.data); }
            });
        })();
        """;

    private readonly IBridgeMessageWriter _writer;
    private readonly IBridgeMessageParser _parser;
    private readonly Dispatcher _dispatcher;
    private readonly WebView2 _webView = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource>> _acknowledgements =
        new(StringComparer.Ordinal);

    private CoreWebView2? _core;
    private int _disposed;

    /// <inheritdoc cref="WebView2TerminalBridge" />
    public WebView2TerminalBridge(IBridgeMessageWriter writer, IBridgeMessageParser parser)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parser);

        _writer = writer;
        _parser = parser;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <inheritdoc />
    public event EventHandler<TerminalInputEventArgs>? InputReceived;

    /// <inheritdoc />
    public event EventHandler<TerminalResizeEventArgs>? ResizeRequested;

    /// <inheritdoc />
    public event EventHandler<TerminalReadyEventArgs>? TerminalReady;

    /// <summary>Контрол, который окно кладёт в свою разметку. Больше о WebView2 никто не знает.</summary>
    public FrameworkElement Control => _webView;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string webRoot = ResolveWebRoot();

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: ResolveUserDataFolder())
                .ConfigureAwait(true);

            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TerminalBridgeUnavailableException(
                "Не удалось поднять WebView2. Проверьте, установлен ли WebView2 Runtime.",
                exception);
        }

        _core = _webView.CoreWebView2;

        _core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        _core.WebMessageReceived += OnWebMessageReceived;
        _core.ProcessFailed += OnProcessFailed;

        await _core.AddScriptToExecuteOnDocumentCreatedAsync(InboxScript).ConfigureAwait(true);

        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _core.NavigationCompleted -= OnNavigationCompleted;

            if (args.IsSuccess)
            {
                navigated.TrySetResult();
            }
            else
            {
                navigated.TrySetException(new TerminalBridgeUnavailableException(
                    $"Страница терминалов не загрузилась: {args.WebErrorStatus}. Каталог «{webRoot}» отдаётся как https://{VirtualHost}/."));
            }
        }

        _core.NavigationCompleted += OnNavigationCompleted;
        _core.Navigate(PageUrl);

        await navigated.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public ValueTask CreateTerminalAsync(TerminalId terminalId, string title, CancellationToken cancellationToken) =>
        PostAsync(_writer.Create(terminalId, title), cancellationToken);

    /// <inheritdoc />
    public ValueTask ShowTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        PostAsync(_writer.Show(terminalId), cancellationToken);

    /// <inheritdoc />
    public async ValueTask CloseTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken)
    {
        await PostAsync(_writer.Close(terminalId), cancellationToken).ConfigureAwait(false);
        ReleaseAcknowledgements(terminalId.Value);
    }

    /// <inheritdoc />
    public async ValueTask WriteOutputAsync(TerminalId terminalId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Полезная нагрузка вычитывается здесь, до первого await: вызывающий вправе вернуть
        // буфер в пул, как только метод отдал управление. Base64 считается на вызывающем потоке,
        // на UI уходит уже готовая строка.
        string message = _writer.Out(terminalId, payload.Span);

        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _acknowledgements.GetOrAdd(terminalId.Value, static _ => new ConcurrentQueue<TaskCompletionSource>());
        queue.Enqueue(acknowledged);

        try
        {
            await PostAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseAcknowledgements(terminalId.Value);
            throw;
        }

        // Завершается по подтверждению страницы: колбэк term.write → сообщение ack.
        // На этом построен backpressure раздела 3.3 ТЗ.
        await acknowledged.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask NotifyExitedAsync(TerminalId terminalId, int exitCode, CancellationToken cancellationToken) =>
        PostAsync(_writer.Exited(terminalId, exitCode), cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (string terminalId in _acknowledgements.Keys)
        {
            ReleaseAcknowledgements(terminalId);
        }

        // Освобождение может прийти и с потока диспетчера (App.OnExit), и со стороннего.
        // Блокирующий InvokeAsync с самого диспетчера повесил бы выход из приложения.
        if (_dispatcher.CheckAccess())
        {
            ReleaseWebView();
            return;
        }

        await _dispatcher.InvokeAsync(ReleaseWebView);
    }

    private void ReleaseWebView()
    {
        if (_core is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            _core = null;
        }

        _webView.Dispose();
    }

    private static string ResolveUserDataFolder()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeAgentsShell",
            "WebView2");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Каталог страницы рядом с исполняемым файлом. Если его нет, маппинг молча отдал бы 404
    /// и пользователь увидел бы чёрный экран — поэтому проверяем заранее и говорим, что делать.
    /// </summary>
    private static string ResolveWebRoot()
    {
        string webRoot = Path.Combine(AppContext.BaseDirectory, "web");

        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new TerminalBridgeUnavailableException(
                $"Не найдена страница терминалов: «{Path.Combine(webRoot, "index.html")}». " +
                "Каталог web должен копироваться в выходной каталог сборки.");
        }

        if (!File.Exists(Path.Combine(webRoot, "vendor", "xterm.js")))
        {
            throw new TerminalBridgeUnavailableException(
                $"Не найдены локальные ассеты xterm.js в «{Path.Combine(webRoot, "vendor")}». " +
                "Выполните «npm install && npm run vendor» в каталоге web и пересоберите решение.");
        }

        return webRoot;
    }

    private ValueTask PostAsync(string message, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            Post(message);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_dispatcher.InvokeAsync(() => Post(message), DispatcherPriority.Send, cancellationToken).Task);
    }

    private void Post(string message) => _core?.PostWebMessageAsString(message);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string json;
        try
        {
            json = args.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            // Страница прислала не строку — для протокола это мусор.
            return;
        }

        if (!_parser.TryParse(json, out var message) || message is null)
        {
            return;
        }

        switch (message)
        {
            case InboundBridgeMessage.Input input:
                InputReceived?.Invoke(this, new TerminalInputEventArgs(input.TerminalId, input.Data));
                break;

            case InboundBridgeMessage.Resize resize:
                ResizeRequested?.Invoke(this, new TerminalResizeEventArgs(resize.TerminalId, resize.Size));
                break;

            case InboundBridgeMessage.Ready ready:
                TerminalReady?.Invoke(this, new TerminalReadyEventArgs(ready.TerminalId));
                break;

            case InboundBridgeMessage.Ack ack:
                CompleteAcknowledgement(ack.TerminalId.Value);
                break;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        // Рендерер упал: подтверждений больше не будет, иначе помпы встанут навсегда.
        foreach (string terminalId in _acknowledgements.Keys)
        {
            ReleaseAcknowledgements(terminalId);
        }
    }

    private void CompleteAcknowledgement(string terminalId)
    {
        if (_acknowledgements.TryGetValue(terminalId, out var queue) && queue.TryDequeue(out var acknowledged))
        {
            acknowledged.TrySetResult();
        }
    }

    private void ReleaseAcknowledgements(string terminalId)
    {
        if (!_acknowledgements.TryGetValue(terminalId, out var queue))
        {
            return;
        }

        while (queue.TryDequeue(out var acknowledged))
        {
            acknowledged.TrySetResult();
        }
    }
}
