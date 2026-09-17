using System.Windows;

namespace ClaudeAgentsShell.App;

/// <summary>Главное окно. Ровно один WebView2 на всё окно — контрол на вкладку недопустим.</summary>
public partial class MainWindow : Window
{
    /// <inheritdoc cref="MainWindow" />
    public MainWindow()
    {
        InitializeComponent();
    }
}
