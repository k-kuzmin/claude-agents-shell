namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Общий словарь приёмника и генератора настроек. Имена живут здесь в одном экземпляре:
/// опечатка в любом из них не ломает сборку, а тихо оставляет вкладку без маркера состояния —
/// то есть выглядит ровно как допустимая деградация раздела 5.3 ТЗ.
/// </summary>
internal static class HookProtocol
{
    /// <summary>Переменная окружения псевдоконсоли, через которую вкладка передаёт свой токен.</summary>
    public const string TokenVariableName = "CLAUDE_AGENTS_SHELL_TOKEN";

    /// <summary>
    /// Заголовок запроса, в котором хук возвращает токен вкладки.
    /// Значение токена обязано быть ASCII: заголовки HTTP не переносят ничего другого,
    /// и клиент отказывается такой запрос отправлять. Токен выдаёт приложение, так что
    /// это ограничение на генератор, а не на пользователя.
    /// </summary>
    public const string TokenHeaderName = "X-Agents-Shell-Token";

    /// <summary>Поле полезной нагрузки с токеном — запасной путь, если заголовок не дошёл.</summary>
    public const string TokenPayloadField = "correlation_token";

    /// <summary>Путь локального endpoint: <c>http://127.0.0.1:&lt;порт&gt;/hook</c>.</summary>
    public const string PathSegment = "hook";
}
