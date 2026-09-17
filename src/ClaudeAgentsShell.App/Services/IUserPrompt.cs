namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Разговор с пользователем модальными окнами: подтверждение и сообщение об ошибке.
/// ViewModel зовёт порт, а не <c>MessageBox</c>, иначе ветку «пользователь отказался»
/// нечем проверить в тестах.
/// </summary>
public interface IUserPrompt
{
    /// <summary>Спрашивает подтверждение. <c>true</c> — пользователь согласился.</summary>
    bool Confirm(string title, string message);

    /// <summary>Показывает сообщение об ошибке, на которое пользователь не может ответить ничем, кроме «ок».</summary>
    void ShowError(string title, string message);
}
