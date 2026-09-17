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
    private bool _shutdownCompleted;

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
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Дескриптор уже есть — можно спросить рабочую область именно того монитора,
        // на котором оказалось окно, и вписаться в неё вместе с масштабом этого монитора.
        WorkAreaPlacement.FitIntoWorkArea(this);
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!_shutdownCompleted)
        {
            // Закрытие отменяется на КАЖДОЙ попытке, пока гашение не закончено, а не только
            // на первой. Иначе повторный клик по крестику закрывал бы окно посреди гашения:
            // App.OnExit освободил бы контейнер, чьи объекты уже помечены освобождёнными и
            // вернулись бы мгновенно, не дождавшись первой цепочки, — и псевдоконсоли
            // пережили бы процесс.
            e.Cancel = true;

            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                _ = ShutdownAsync();
            }
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
        catch (Exception exception) when (exception is TerminalBridgeUnavailableException
                                             or ShellNotFoundException)
        {
            // Страницы терминалов нет либо в системе не нашлось ни одной оболочки — обе ветки
            // предусмотрены контрактом и не должны валить процесс из async void. Ловим точные
            // типы: перехват InvalidOperationException накрыл бы и дефекты потоков WPF,
            // показав их пользователю как «нет оболочки».
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
            // иначе крестик перестанет работать совсем. Снятый флаг пропускает
            // повторный вход в OnClosing без отмены.
            _shutdownCompleted = true;
            Close();
        }
    }
}
