using System.Windows;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Реализация диалогов через <see cref="MessageBox"/>.</summary>
public sealed class MessageBoxUserPrompt : IUserPrompt
{
    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            Owner(),
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <inheritdoc />
    /// <remarks>
    /// Единственный метод, переживающий отсутствие главного окна. Сообщение об ошибке
    /// показывает и глобальный обработчик сбоев: упасть можно и в конструкторе окна, и
    /// тогда владельца ещё нет, а сообщение нужно тем более.
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
