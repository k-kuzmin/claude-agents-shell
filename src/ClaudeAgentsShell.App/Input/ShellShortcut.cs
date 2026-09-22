namespace ClaudeAgentsShell.App.Input;

/// <summary>Оконная команда, назначенная сочетанию клавиш.</summary>
public enum ShellShortcut
{
    /// <summary>Сочетание окну не адресовано — клавиша уходит в терминал без изменений.</summary>
    None = 0,

    /// <summary>Новая сессия в активном проекте (<c>Ctrl+Shift+T</c>).</summary>
    NewSession = 1,

    /// <summary>Закрыть активную вкладку (<c>Ctrl+Shift+W</c>).</summary>
    CloseTab = 2,

    /// <summary>Следующая вкладка (<c>Ctrl+Tab</c>).</summary>
    NextTab = 3,

    /// <summary>Предыдущая вкладка (<c>Ctrl+Shift+Tab</c>).</summary>
    PreviousTab = 4,

    /// <summary>Вкладка по номеру (<c>Ctrl+1..9</c>).</summary>
    SelectTab = 5,

    /// <summary>Панель diff активной вкладки (<c>Ctrl+Shift+D</c>, issue #5).</summary>
    ShowDiff = 6,
}
