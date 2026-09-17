using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App;

/// <summary>Главное окно. Ровно один WebView2 на всё окно — контрол на вкладку недопустим.</summary>
public partial class MainWindow : Window
{
    private readonly WebView2TerminalBridge _bridge;
    private readonly ShellViewModel _shell;
    private readonly ShellShortcutHandler _shortcuts;

    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    /// <inheritdoc cref="MainWindow" />
    public MainWindow(WebView2TerminalBridge bridge, ShellViewModel shell, ShellShortcutHandler shortcuts)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(shortcuts);

        InitializeComponent();

        _bridge = bridge;
        _shell = shell;
        _shortcuts = shortcuts;

        DataContext = _shell;
        TerminalHost.Children.Add(_bridge.Control);
        Loaded += OnLoaded;
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Дескриптор уже есть — можно спросить рабочую область именно того монитора,
        // на котором оказалось окно, и вписаться в неё вместе с масштабом этого монитора.
        WorkAreaPlacement.FitIntoWorkArea(this);

        // Своё обрамление означает своё поведение при разворачивании: без этого окно
        // без системной рамки растягивается на весь экран и накрывает панель задач —
        // ровно тот дефект, который нашёлся на приёмке M1.
        WorkAreaPlacement.KeepMaximizedWithinWorkArea(this);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Автоповтор удержанной клавиши окну не адресован: удержанный Ctrl+Shift+W иначе
        // успевал бы поставить второе подтверждение поверх первого. В терминал автоповтор
        // уходит как обычно — это событие остаётся необработанным.
        if (e.IsRepeat && ShellShortcutMap.TryMap(e.Key, Keyboard.Modifiers, out _, out _))
        {
            e.Handled = true;
            return;
        }

        // Туннелирование: оконные сочетания перехватываются до того, как их увидит терминал.
        // Всё остальное остаётся необработанным и доходит до оболочки без изменений.
        if (_shortcuts.Handle(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
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

    // Кнопки своего обрамления ходят теми же системными командами, что и штатные:
    // «закрыть» доходит до OnClosing обычным WM_CLOSE, а не зовёт Close() в обход
    // логики гашения.
    private void OnMinimizeWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnMaximizeWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.MaximizeWindow(this);

    private void OnRestoreWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.RestoreWindow(this);

    private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _shell.InitializeAsync(CancellationToken.None);
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
            // Набором вкладок владеет корневая ViewModel — она же его и гасит.
            await _shell.DisposeAsync();
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
