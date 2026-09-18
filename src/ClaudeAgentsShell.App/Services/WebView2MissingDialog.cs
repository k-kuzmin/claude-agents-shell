using System.Windows;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.App.Views;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Показывает <see cref="WebView2MissingWindow"/> поверх главного окна.</summary>
public sealed class WebView2MissingDialog : IWebView2MissingDialog
{
    private readonly IUrlLauncher _urlLauncher;

    /// <inheritdoc cref="WebView2MissingDialog" />
    public WebView2MissingDialog(IUrlLauncher urlLauncher)
    {
        ArgumentNullException.ThrowIfNull(urlLauncher);
        _urlLauncher = urlLauncher;
    }

    /// <inheritdoc />
    public void Show(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var window = new WebView2MissingWindow(new WebView2MissingViewModel(failure, _urlLauncher))
        {
            // Владелец берётся у приложения, а не хранится ссылкой: иначе порт знал бы
            // про жизненный цикл главного окна и удерживал бы его живым.
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        // Модально: пока пользователь читает, за спиной не должно происходить ничего —
        // окно всё равно не работает, а после закрытия приложение завершается.
        window.ShowDialog();
    }
}
