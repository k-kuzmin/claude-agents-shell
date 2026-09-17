namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Выбор каталога пользователем. Отдельный порт, потому что ViewModel не имеет права
/// открывать системные диалоги: без него «добавить проект» нельзя было бы протестировать.
/// </summary>
public interface IFolderPicker
{
    /// <summary>Показывает диалог выбора папки. Возвращает путь либо <c>null</c>, если пользователь отказался.</summary>
    string? PickFolder(string title);
}
