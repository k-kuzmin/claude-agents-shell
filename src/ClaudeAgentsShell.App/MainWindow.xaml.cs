using System.ComponentModel;
using System.Windows;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Terminal;

namespace ClaudeAgentsShell.App;

/// <summary>Главное окно. Ровно один WebView2 на всё окно — контрол на вкладку недопустим.</summary>
public partial class MainWindow : Window
{
    private readonly WebView2TerminalBridge _bridge;
    private readonly TerminalWorkspace _workspace;

    private bool _shutdownStarted;

    /// <inheritdoc cref="MainWindow" />
    public MainWindow(WebView2TerminalBridge bridge, TerminalWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(workspace);

        InitializeComponent();

        _bridge = bridge;
        _workspace = workspace;

        Root.Children.Add(_bridge.Control);
        Loaded += OnLoaded;
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!_shutdownStarted)
        {
            // Сначала детерминированно гасим псевдоконсоли и страницу, только потом закрываемся:
            // иначе процесс оболочки может пережить окно.
            _shutdownStarted = true;
            e.Cancel = true;
            _ = ShutdownAsync();
        }

        base.OnClosing(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _workspace.StartAsync(CancellationToken.None);
        }
        catch (TerminalBridgeUnavailableException exception)
        {
            // Без страницы терминалов работать не с чем; показываем причину, а не чёрный экран.
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            // Помпы освобождаются раньше моста: им нужно дождаться подтверждений страницы.
            await _workspace.DisposeAsync();
            await _bridge.DisposeAsync();
        }
        finally
        {
            // Что бы ни случилось при освобождении, окно должно закрыться:
            // иначе крестик перестанет работать совсем.
            Close();
        }
    }
}
