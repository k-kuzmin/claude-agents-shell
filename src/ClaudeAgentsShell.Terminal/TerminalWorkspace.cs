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

        foreach (var pump in _pumps.Values)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }

        _pumps.Clear();
        _pending.Clear();
        _cts.Dispose();
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

            if (_pumps.TryAdd(args.TerminalId.Value, pump))
            {
                pump.Start();
            }
            else
            {
                _ = pump.DisposeAsync();
            }
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
        catch (OperationCanceledException)
        {
            // Приложение закрывается.
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
