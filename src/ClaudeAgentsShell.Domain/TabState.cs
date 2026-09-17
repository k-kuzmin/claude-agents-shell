namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Состояние вкладки. Источник — хуки Claude Code, а не разбор вывода.
/// Если хуки не сработали, состояние остаётся <see cref="Unknown"/> — это допустимая деградация.
/// </summary>
public enum TabState
{
    /// <summary>Маркера нет: хуки не подключились или сессия запущена мимо приложения.</summary>
    Unknown = 0,

    /// <summary>Простаивает: сессия живёт, но агент не работал и ввода не ждёт.</summary>
    Idle = 1,

    /// <summary>Работает: агент занят.</summary>
    Busy = 2,

    /// <summary>Ждёт ввода: агент закончил ответ (хук <c>Stop</c>) и пользователь ещё не ответил.</summary>
    AwaitingInput = 3,
}
