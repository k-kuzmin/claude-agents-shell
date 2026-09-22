using System.Windows;
using System.Windows.Input;

namespace ClaudeAgentsShell.App.Views;

/// <summary>
/// Модальное подтверждение действия: вопрос и пара кнопок «отмена / подтвердить».
/// Заменяет системный <see cref="MessageBox"/> там, где он выбивался из темы приложения.
/// Логики у окна нет — формулировки приносит вызывающий.
/// </summary>
/// <remarks>
/// Показывается только через <see cref="Window.ShowDialog"/>: ответ отдаётся через
/// <see cref="Window.DialogResult"/>, а он разрешён лишь у модального окна.
/// </remarks>
public partial class ConfirmationWindow : Window
{
    /// <inheritdoc cref="ConfirmationWindow" />
    /// <param name="title">Заголовок окна: коротко о действии, например «Убрать проект».</param>
    /// <param name="message">Сам вопрос и его последствия.</param>
    public ConfirmationWindow(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        InitializeComponent();

        // Два текста и ничего больше: ради них ViewModel заводить не за что, а привязка
        // к посреднику только добавила бы место, где надписи могут разъехаться.
        Title = title;
        CaptionText.Text = title;
        MessageText.Text = message;

        Loaded += OnLoaded;
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Enter на кнопке отказа обязан означать отказ. Кнопка подтверждения помечена
        // IsDefault, и одного этого хватило бы, чтобы Enter сработал мимо фокуса; прежний
        // MessageBox вёл себя наоборот — кнопкой по умолчанию у него стояла «Отмена».
        // Случай разбирается явно и на этапе просмотра события: обработчик на всплытии
        // получил бы управление уже после кнопки по умолчанию.
        if (e.Key == Key.Enter && ReferenceEquals(Keyboard.FocusedElement, CancelButton))
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }

        base.OnPreviewKeyDown(e);
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

    // Присваивание DialogResult само закрывает модальное окно.
    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    // Крестик — это отказ: результат не выставляется, и ShowDialog вернёт null.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Фокус на отказе: согласие должно требовать осознанного шага к нему.
        CancelButton.Focus();
    }
}
