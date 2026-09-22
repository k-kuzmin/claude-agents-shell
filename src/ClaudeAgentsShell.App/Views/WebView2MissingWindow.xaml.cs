using System.Windows;
using System.Windows.Input;
using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.Views;

/// <summary>
/// Окно «не установлен движок терминалов» (раздел 8 ТЗ). Показывается вместо стек-трейса:
/// объяснение, ссылка на установщик и раскрывающийся блок подробностей.
/// </summary>
public partial class WebView2MissingWindow : Window
{
    /// <inheritdoc cref="WebView2MissingWindow" />
    public WebView2MissingWindow(WebView2MissingViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Escape закрывает окно. Через Close(), а не через IsCancel у кнопки: DialogResult
        // разрешено присваивать только окну, показанному как модальное, и окно не должно
        // зависеть от того, каким способом его показали.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Перетаскивание за полосу заголовка. Своё обрамление означает и своё перетаскивание:
    /// системной полосы у окна нет.
    /// </summary>
    private void OnCaptionBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove бросает, если кнопка уже отпущена, — состояние проверяется явно.
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
