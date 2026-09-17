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
    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    // Владелец берётся у приложения, а не хранится ссылкой на окно: иначе порт диалогов
    // удерживал бы окно живым и знал бы про его жизненный цикл.
    private static Window Owner() =>
        System.Windows.Application.Current?.MainWindow
        ?? throw new InvalidOperationException("Главное окно ещё не создано.");
}
