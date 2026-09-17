namespace ClaudeAgentsShell.Domain;

/// <summary>Хук Claude Code, на который подписано приложение.</summary>
public enum HookKind
{
    /// <summary>Неизвестный или незарегистрированный хук — игнорируется.</summary>
    Unknown = 0,

    /// <summary>Сессия стартовала: приносит <c>session_id</c>, по которому вкладка узнаёт себя.</summary>
    SessionStart = 1,

    /// <summary>Агент закончил ответ — вкладка переходит в «ждёт ввода».</summary>
    Stop = 2,

    /// <summary>Сессия завершена — маркер состояния снимается.</summary>
    SessionEnd = 3,
}

/// <summary>Событие от хука, пришедшее на локальный endpoint приложения.</summary>
/// <param name="Kind">Какой хук сработал.</param>
/// <param name="SessionId">Идентификатор сессии Claude Code, если пришёл в полезной нагрузке.</param>
/// <param name="WorkingDirectory">Рабочий каталог сессии, если пришёл в полезной нагрузке.</param>
/// <param name="CorrelationToken">
/// Токен, выданный приложением конкретной вкладке при запуске и возвращённый хуком.
/// Нужен, чтобы сопоставить событие с вкладкой, не полагаясь на совпадение каталогов.
/// </param>
/// <param name="ReceivedUtc">Время приёма события.</param>
public sealed record HookEvent(
    HookKind Kind,
    string? SessionId,
    string? WorkingDirectory,
    string? CorrelationToken,
    DateTimeOffset ReceivedUtc);
