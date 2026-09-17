using System.Collections.Concurrent;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Output;

namespace ClaudeAgentsShell.Terminal;

/// <summary>
/// Связывает мост и псевдоконсоли: маршрутизирует сообщения страницы по идентификатору вкладки
/// и держит помпу на каждую открытую вкладку. В M1 открывается ровно одна вкладка, но
/// маршрутизация с самого начала рассчитана на N терминалов — так устроена страница (раздел 3.1 ТЗ).
/// </summary>
public sealed class TerminalWorkspace : IAsyncDisposable
{
    private readonly ITerminalBridge _bridge;
    private readonly IPtySessionFactory _ptyFactory;
    private readonly IShellResolver _shells;
    private readonly TerminalOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TerminalPump> _pumps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PtyStartInfo> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private int _disposed;

    /// <inheritdoc cref="TerminalWorkspace" />
    public TerminalWorkspace(
        ITerminalBridge bridge,
        IPtySessionFactory ptyFactory,
        IShellResolver shells,
        TerminalOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(ptyFactory);
        ArgumentNullException.ThrowIfNull(shells);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _bridge = bridge;
        _ptyFactory = ptyFactory;
        _shells = shells;
        _options = options;
        _timeProvider = timeProvider;

        _bridge.TerminalReady += OnTerminalReady;
        _bridge.InputReceived += OnInputReceived;
        _bridge.ResizeRequested += OnResizeRequested;
    }

    /// <summary>
    /// Поднимает страницу терминалов и открывает стартовую вкладку с оболочкой.
    /// <c>claude</c> в M1 пользователь запускает внутри терминала руками.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _bridge.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await OpenAsync(ShellKind.Pwsh, DefaultWorkingDirectory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Открывает новую вкладку: просит страницу создать терминал и ждёт от неё <c>ready</c>.</summary>
    public async Task<TerminalId> OpenAsync(ShellKind preferredShell, string workingDirectory, CancellationToken cancellationToken)
    {
        var shell = _shells.Resolve(preferredShell);
        var terminalId = TerminalId.New();

        var startInfo = new PtyStartInfo(
            shell,
            workingDirectory,
            TerminalSize.Default,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TERM"] = "xterm-256color",
            });

        _pending[terminalId.Value] = startInfo;

        string title = $"{shell.Kind} · {workingDirectory}";
        await _bridge.CreateTerminalAsync(terminalId, title, cancellationToken).ConfigureAwait(false);
        await _bridge.ShowTerminalAsync(terminalId, cancellationToken).ConfigureAwait(false);

        return terminalId;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _bridge.TerminalReady -= OnTerminalReady;
        _bridge.InputReceived -= OnInputReceived;
        _bridge.ResizeRequested -= OnResizeRequested;

        await _cts.CancelAsync().ConfigureAwait(false);

        // Параллельно: у каждой вкладки свой бюджет ожидания выхода процесса, и последовательное
        // закрытие умножало бы его на число вкладок, держа окно на экране всё это время.
        await Task.WhenAll(_pumps.Keys
                .Select(id => CloseAsync(new TerminalId(id), notifyPage: false)))
            .ConfigureAwait(false);

        _pending.Clear();
        _cts.Dispose();
    }

    /// <summary>
    /// Закрывает вкладку: гасит псевдоконсоль, убирает помпу из маршрутизации и просит
    /// страницу уничтожить терминал. Единственный путь удаления вкладки — здесь же
    /// снимаются ожидания записи, иначе они копились бы на каждой закрытой вкладке.
    /// </summary>
    public async Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        await CloseAsync(terminalId, notifyPage: true, cancellationToken).ConfigureAwait(false);

    private async Task CloseAsync(TerminalId terminalId, bool notifyPage, CancellationToken cancellationToken = default)
    {
        _pending.TryRemove(terminalId.Value, out _);

        if (_pumps.TryRemove(terminalId.Value, out var pump))
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }

        if (notifyPage)
        {
            await _bridge.CloseTerminalAsync(terminalId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Оболочка завершилась сама. Псевдоконсоль и помпу освобождаем сразу, а терминал
    /// на странице оставляем: пользователь должен увидеть код выхода (раздел 8 ТЗ).
    /// Выполняется не на потоке колбэка завершения процесса — освобождение сессии
    /// дожидается этого колбэка и на нём же заблокировалось бы.
    /// </summary>
    private void OnShellExited(TerminalId terminalId) =>
        _ = Task.Run(() => CloseAsync(terminalId, notifyPage: false));

    private async Task ReleaseDuplicateAsync(TerminalId terminalId, TerminalPump pump)
    {
        await pump.DisposeAsync().ConfigureAwait(false);
        await _bridge.CloseTerminalAsync(terminalId, CancellationToken.None).ConfigureAwait(false);
    }

    private static string DefaultWorkingDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void OnTerminalReady(object? sender, TerminalReadyEventArgs args)
    {
        if (!_pending.TryRemove(args.TerminalId.Value, out var startInfo))
        {
            return;
        }

        try
        {
            // Псевдоконсоль поднимается синхронно, до возврата из обработчика: следом за ready
            // страница пришлёт resize, и помпа к этому моменту уже зарегистрирована.
            var session = _ptyFactory.Create(startInfo);
            var pump = new TerminalPump(args.TerminalId, session, _bridge, _options, _timeProvider);

            if (!_pumps.TryAdd(args.TerminalId.Value, pump))
            {
                // Вкладка с таким идентификатором уже жива: освобождаем лишнюю помпу целиком
                // и через общий путь закрытия, чтобы не осталось ни псевдоконсоли, ни учёта.
                _ = ReleaseDuplicateAsync(args.TerminalId, pump);
                return;
            }

            var terminalId = args.TerminalId;

            // Оболочка может завершиться сама (пользователь набрал exit, процесс упал).
            // Без этой подписки псевдоконсоль оставалась бы открытой до закрытия приложения:
            // ConPtySession достижим из _pumps, и финализатор SafeHandle не сработает.
            // Сама вкладка на странице при этом остаётся — с пометкой о коде выхода
            // (раздел 8 ТЗ), поэтому страницу закрывать терминал не просим.
            session.Exited += (_, _) => OnShellExited(terminalId);

            pump.Start();
        }
        catch (PtyStartException exception)
        {
            // Оболочку поднять не удалось — пользователь должен увидеть причину прямо в терминале,
            // а не в молчаливо закрытой вкладке (раздел 8 ТЗ).
            ReportToTerminal(args.TerminalId, exception.Message);
        }
    }

    private async void OnInputReceived(object? sender, TerminalInputEventArgs args)
    {
        if (!_pumps.TryGetValue(args.TerminalId.Value, out var pump))
        {
            return;
        }

        try
        {
            await pump.SendInputAsync(args.Data, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or ObjectDisposedException
                                              or IOException)
        {
            // Вкладку закрыли, пока пользователь печатал. Потерять символ допустимо,
            // уронить процесс из async void — нет.
        }
    }

    private void ReportToTerminal(TerminalId terminalId, string message)
    {
        const string Red = "\u001b[31m";
        const string Reset = "\u001b[0m\r\n";

        byte[] bytes = Encoding.UTF8.GetBytes(Red + message + Reset);
        _ = _bridge.WriteOutputAsync(terminalId, bytes, CancellationToken.None);
    }

    private void OnResizeRequested(object? sender, TerminalResizeEventArgs args)
    {
        if (_pumps.TryGetValue(args.TerminalId.Value, out var pump))
        {
            pump.Resize(args.Size);
        }
    }
}
