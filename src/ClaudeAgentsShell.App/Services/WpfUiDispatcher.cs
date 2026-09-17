using System.Windows.Threading;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Перевод работы в поток интерфейса через <see cref="Dispatcher"/> WPF.</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <inheritdoc cref="WpfUiDispatcher" />
    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>Диспетчер потока, в котором создан контейнер, — то есть потока интерфейса.</summary>
    public WpfUiDispatcher() : this(Dispatcher.CurrentDispatcher)
    {
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            // Уже в потоке интерфейса: выполняем сразу, чтобы не плодить лишний кадр задержки.
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }
}
