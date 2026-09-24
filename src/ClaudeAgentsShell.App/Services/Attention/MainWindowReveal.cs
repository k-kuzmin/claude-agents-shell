using System.Windows;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Показывает главное окно: из свёрнутого восстанавливает в прежнее состояние (развёрнутое
/// остаётся развёрнутым) и активирует. Вывод на передний план при активации из уведомления
/// решает ОС; обходных трюков здесь нет. Вызывать только из потока интерфейса.
/// </summary>
public sealed class MainWindowReveal : IMainWindowReveal
{
    private readonly Func<Window?> _window;

    /// <param name="window">
    /// Главное окно, берётся лениво при каждом вызове. Делегат не должен создавать окно:
    /// пока его нет, показывать нечего и вызов ничего не делает.
    /// </param>
    public MainWindowReveal(Func<Window?> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <inheritdoc />
    public void Reveal()
    {
        if (_window() is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            // SC_RESTORE возвращает окно в состояние до сворачивания, в том числе развёрнутое;
            // прямое присваивание Normal разворачивало бы его в обычный размер.
            SystemCommands.RestoreWindow(window);
        }

        window.Activate();
    }
}
