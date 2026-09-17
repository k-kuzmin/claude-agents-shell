using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal;
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
    private readonly TerminalOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly WebView2 _webView = new();
    private readonly ConcurrentDictionary<string, PendingWriteRegistry> _acknowledgements = new(StringComparer.Ordinal);

    private CoreWebView2? _core;
    private int _disposed;

    /// <inheritdoc cref="WebView2TerminalBridge" />
    public WebView2TerminalBridge(IBridgeMessageWriter writer, IBridgeMessageParser parser, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(options);

        _writer = writer;
        _parser = parser;
        _options = options;
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

        ApplySettings(_core.Settings);

        _core.WebMessageReceived += OnWebMessageReceived;
        _core.ProcessFailed += OnProcessFailed;

        await _core.AddScriptToExecuteOnDocumentCreatedAsync(InboxScript).ConfigureAwait(true);
        await _core.AddScriptToExecuteOnDocumentCreatedAsync(BuildConfigScript(_options)).ConfigureAwait(true);

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
        try
        {
            await PostAsync(_writer.Close(terminalId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Даже если пост не удался (диспетчер гасится, токен отменён), учёт вкладки
            // обязан уйти — иначе запись словаря переживает вкладку.
            ReleaseAcknowledgements(terminalId.Value);
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteOutputAsync(TerminalId terminalId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var pending = _acknowledgements.GetOrAdd(terminalId.Value, static _ => new PendingWriteRegistry());
        long sequence = pending.Reserve(out var acknowledged);

        try
        {
            // Полезная нагрузка вычитывается здесь, до первого await: вызывающий вправе вернуть
            // буфер в пул, как только метод отдал управление. Base64 считается на вызывающем
            // потоке, на UI уходит уже готовая строка. Сборка сообщения внутри try, чтобы
            // отказ писателя не оставил резервацию в учёте.
            string message = _writer.Out(terminalId, sequence, payload.Span);

            await PostAsync(message, cancellationToken).ConfigureAwait(false);

            // Завершается по подтверждению страницы: колбэк term.write → сообщение ack.
            // На этом построен backpressure раздела 3.3 ТЗ.
            await acknowledged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Брошенное ожидание убирается из учёта: иначе следующая квитанция завершила бы
            // чужую запись и счётчик незавершённых поехал бы навсегда.
            pending.Abandon(sequence);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask NotifyExitedAsync(TerminalId terminalId, int exitCode, CancellationToken cancellationToken)
    {
        try
        {
            await PostAsync(_writer.Exited(terminalId, exitCode), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Пачки вкладки производит только её помпа, а она к этому моменту уже закончила:
            // ждать подтверждений больше нечего. Терминал на странице при этом остаётся —
            // пользователь должен увидеть код выхода (раздел 8 ТЗ).
            ReleaseAcknowledgements(terminalId.Value);
        }
    }

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

    /// <summary>
    /// Окно терминала не должно вести себя как браузер. Особенно важны горячие клавиши:
    /// F5 и Ctrl+R перезагрузили бы страницу, карта терминалов обнулилась бы, а C# об этом
    /// не узнал бы — вкладка осталась бы мёртвой навсегда.
    /// </summary>
    private static void ApplySettings(CoreWebView2Settings settings)
    {
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
    }

    /// <summary>
    /// Отдаёт странице настройки из <see cref="TerminalOptions"/> до создания документа.
    /// Источник правды один — C#; дублировать значения константами в app.js нельзя.
    /// Это не сообщение моста: протокол раздела 3.2 ТЗ не расширяется.
    /// </summary>
    private static string BuildConfigScript(TerminalOptions options)
    {
        int scrollback = options.Scrollback;
        int resizeDebounce = (int)options.ResizeDebounce.TotalMilliseconds;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"window.__terminalConfig = {{ scrollback: {scrollback}, resizeDebounceMs: {resizeDebounce} }};");
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

        // Background ниже Render: шумный вывод терминалов не должен вытеснять отрисовку.
        return new ValueTask(
            _dispatcher.InvokeAsync(() => Post(message), DispatcherPriority.Background, cancellationToken).Task);
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
                CompleteAcknowledgement(ack.TerminalId.Value, ack.Sequence);
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

    private void CompleteAcknowledgement(string terminalId, long sequence)
    {
        if (_acknowledgements.TryGetValue(terminalId, out var pending))
        {
            pending.CompleteUpTo(sequence);
        }
    }

    /// <summary>
    /// Отпускает все ожидания вкладки и убирает её из учёта. Вызывается, когда подтверждений
    /// больше не будет: вкладка закрыта, мост освобождён, рендерер упал.
    /// </summary>
    private void ReleaseAcknowledgements(string terminalId)
    {
        if (_acknowledgements.TryRemove(terminalId, out var pending))
        {
            pending.ReleaseAll();
        }
    }
}
