using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Переводит хуки в сигнал «агент мог поменять файлы» для плашки «есть изменения» (issue #5):
/// панель — снимок, и опроса рабочего дерева нет, поэтому единственный источник — хуки.
/// </summary>
/// <remarks>
/// Считается только <see cref="HookKind.PostToolBatch"/> — и главного агента, и сабагентов:
/// хуки сабагента приходят с токеном родительской вкладки, а менять файлы он может так же.
/// <see cref="HookKind.Stop"/> не считается: любая правка файла — инструмент, и его пачка уже
/// дала сигнал, а <c>Stop</c> без пачки (агент только писал текст, или ход закончился сразу
/// после <c>show_diff</c>) ставил бы плашку на снимок, в котором ничего не менялось.
/// Хук приходит из пула <c>HttpListener</c> — сигнал уходит в поток интерфейса.
/// </remarks>
public sealed class DiffStaleTracker : IDisposable
{
    private readonly IHookListener _hooks;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiffChangeSink _sink;
    private bool _subscribed;
    private volatile bool _disposed;

    /// <inheritdoc cref="DiffStaleTracker" />
    /// <param name="hooks">Приёмник хуков.</param>
    /// <param name="dispatcher">Поток интерфейса.</param>
    /// <param name="sink">Кому сообщать: координатор diff.</param>
    public DiffStaleTracker(IHookListener hooks, IUiDispatcher dispatcher, IDiffChangeSink sink)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(sink);

        _hooks = hooks;
        _dispatcher = dispatcher;
        _sink = sink;
    }

    /// <summary>Начинает слушать хуки. Повторный вызов ничего не делает.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscribed)
        {
            return;
        }

        _hooks.HookReceived += OnHookReceived;
        _subscribed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            _hooks.HookReceived -= OnHookReceived;
            _subscribed = false;
        }
    }

    private void OnHookReceived(object? sender, HookEventArgs e)
    {
        if (e.Event.Kind != HookKind.PostToolBatch)
        {
            return;
        }

        var token = e.Event.CorrelationToken;
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                _sink.NotifyFilesChanged(token);
            }
        });
    }
}
