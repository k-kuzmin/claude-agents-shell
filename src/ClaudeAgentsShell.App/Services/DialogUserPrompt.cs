using System.Windows;
using ClaudeAgentsShell.App.Views;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Реализация <see cref="IUserPrompt"/>: вопрос задаётся своим окном в теме приложения,
/// сообщение об ошибке — системным <see cref="MessageBox"/>.
/// </summary>
public sealed class DialogUserPrompt : IUserPrompt
{
    /// <inheritdoc />
    /// <remarks>
    /// Метод остаётся синхронным намеренно: <see cref="Window.ShowDialog"/> крутит вложенный
    /// цикл сообщений ровно так же, как это делал <see cref="MessageBox"/>. На этом держатся
    /// защиты от повторного входа у вызывающих — пока вопрос на экране, они успевают увидеть,
    /// что действие уже выполнено. Асинхронный ответ их молча сломал бы.
    /// </remarks>
    public bool Confirm(string title, string message)
    {
        var window = new ConfirmationWindow(title, message) { Owner = Owner() };

        // Отказ приходит и без результата — крестиком или Alt+F4, поэтому сравнение
        // с true: у невыставленного DialogResult значение null.
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Единственный метод, оставшийся на системном окне, и единственный, переживающий
    /// отсутствие главного окна. Сообщение об ошибке показывает и глобальный обработчик
    /// сбоев: упасть можно и в конструкторе окна — тогда владельца ещё нет, а своё окно
    /// с темой рискует бросить исключение прямо внутри обработчика падения.
    /// </remarks>
    public void ShowError(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // Владелец берётся у приложения, а не хранится ссылкой на окно: иначе порт диалогов
    // удерживал бы окно живым и знал бы про его жизненный цикл. Для вопроса окно
    // обязательно: спрашивают только по действию пользователя в уже открытом окне.
    private static Window Owner() =>
        System.Windows.Application.Current?.MainWindow
        ?? throw new InvalidOperationException("Главное окно ещё не создано.");
}
