namespace ClaudeAgentsShell.Domain;

/// <summary>Хук Claude Code, на который подписано приложение.</summary>
public enum HookKind
{
    /// <summary>Неизвестный или незарегистрированный хук — игнорируется.</summary>
    Unknown = 0,

    /// <summary>Сессия стартовала: приносит <c>session_id</c>, по которому вкладка узнаёт себя.</summary>
    /// <remarks>
    /// Приходит не только на запуск: <c>source</c> различает <c>startup</c>, <c>resume</c>,
    /// <c>clear</c>, <c>compact</c> и <c>fork</c>. Сжатие контекста происходит **посреди хода**,
    /// поэтому границей хода этот хук является не всегда — см. <see cref="HookEvent.Source"/>.
    /// </remarks>
    SessionStart = 1,

    /// <summary>Агент закончил ответ — вкладка переходит в «ждёт ввода».</summary>
    Stop = 2,

    /// <summary>Сессия завершена — маркер состояния снимается.</summary>
    SessionEnd = 3,

    /// <summary>
    /// Пользователь отправил промпт — вкладка переходит в «работает».
    /// </summary>
    /// <remarks>
    /// Заменил собой отслеживание нажатий клавиш в терминале. Клавиатура источником состояния
    /// быть не может: страница отдаёт одним каналом с нажатиями ещё и ответы терминала на запросы
    /// программы, и переключение вкладок выглядело как ввод. Раздел 7 CLAUDE.md требует, чтобы
    /// источником состояния были хуки, — этот хук им и является.
    /// <para>
    /// Срабатывает на отправку, а не на первый символ, поэтому вкладка остаётся «ждёт ввода»,
    /// пока промпт набирается. Это точнее буквы раздела 5.3 ТЗ: пока не отправлено, агент
    /// действительно ждёт.
    /// </para>
    /// <para>
    /// Автором промпта бывает не человек: <c>source</c> различает <c>user</c>, <c>sdk</c>,
    /// <c>system</c>, <c>loop_wakeup</c>, <c>schedule_wakeup</c>, <c>poll_event</c>. В «работает»
    /// переводят все они — ход начинается в любом случае.
    /// </para>
    /// </remarks>
    UserPromptSubmit = 4,

    /// <summary>Сабагент запущен — его <c>agent_id</c> попадает в учёт живых сабагентов вкладки.</summary>
    SubagentStart = 5,

    /// <summary>Сабагент завершился — его <c>agent_id</c> снимается с учёта.</summary>
    /// <remarks>
    /// Учёт по <c>agent_id</c> — запасной путь. Основной источник фоновой работы — поле
    /// <c>background_tasks</c> у <see cref="Stop"/>: оно перечисляет живые задачи само, а его
    /// пустой список заодно очищает учёт. Без поля (старая версия Claude Code или сбой разбора)
    /// потерянный <c>SubagentStop</c> держит вкладку в <see cref="TabState.BackgroundWork"/>
    /// до ближайшей границы сессии.
    /// </remarks>
    SubagentStop = 6,

    /// <summary>
    /// Ход агента оборвался ошибкой или отменой — вкладка переходит туда же, куда по
    /// <see cref="Stop"/>.
    /// </summary>
    /// <remarks>
    /// Отдельный хук, а не разновидность <see cref="Stop"/>: в Claude Code это самостоятельный
    /// путь вызова (<c>executeStopFailureHooks</c>), и на оборванном ходе <see cref="Stop"/>
    /// может не прийти вовсе. Без него вкладка, чей ход упал на ошибке API или был прерван
    /// пользователем, осталась бы в «работает» навсегда: <c>UserPromptSubmit</c> уже был,
    /// а конца хода нет.
    /// </remarks>
    StopFailure = 7,

    /// <summary>
    /// Закончилась пачка параллельных вызовов инструментов — доказательство, что агент работает.
    /// </summary>
    /// <remarks>
    /// Широкий вход в «работает»: срабатывает независимо от того, как начался ход
    /// (промпт, <c>!</c>-команда, пробуждение по фоновой задаче). Хук сабагента приносит
    /// <see cref="HookEvent.AgentId"/>, хук главного потока — нет. Выбран вместо
    /// <c>PreToolUse</c>: тот при недоступном приёмнике отказывает инструменту (fail closed).
    /// </remarks>
    PostToolBatch = 8,

    /// <summary>
    /// Claude Code показал диалог разрешения на инструмент — вкладка ждёт человека.
    /// </summary>
    /// <remarks>
    /// Приходит сразу при показе диалога, в отличие от <c>Notification(permission_prompt)</c>,
    /// который запаздывает на 6 секунд. Может прийти и от сабагента.
    /// </remarks>
    PermissionRequest = 9,
}

/// <summary>Живая фоновая задача сессии из поля <c>background_tasks</c> полезной нагрузки.</summary>
/// <param name="Id">Идентификатор задачи, если пришёл.</param>
/// <param name="Type">
/// Тип задачи: <c>subagent</c>, <c>shell</c>, <c>monitor</c>, <c>workflow</c>, <c>teammate</c>
/// и другие. Список открытый; расписания (<c>session_crons</c>) сюда не входят.
/// </param>
public sealed record BackgroundTask(string? Id, string? Type);

/// <summary>Событие от хука, пришедшее на локальный endpoint приложения.</summary>
/// <param name="Kind">Какой хук сработал.</param>
/// <param name="SessionId">Идентификатор сессии Claude Code, если пришёл в полезной нагрузке.</param>
/// <param name="WorkingDirectory">Рабочий каталог сессии, если пришёл в полезной нагрузке.</param>
/// <param name="CorrelationToken">
/// Токен, выданный приложением конкретной вкладке при запуске и возвращённый хуком.
/// Нужен, чтобы сопоставить событие с вкладкой, не полагаясь на совпадение каталогов.
/// </param>
/// <param name="ReceivedUtc">Время приёма события.</param>
/// <param name="Source">
/// Поле <c>source</c> полезной нагрузки: чем вызван хук. У <c>SessionStart</c> это
/// <c>startup</c>, <c>resume</c>, <c>clear</c>, <c>compact</c> или <c>fork</c>;
/// у <c>UserPromptSubmit</c> — кто автор промпта. Остальные хуки поля не приносят.
/// </param>
/// <param name="AgentId">
/// Поле <c>agent_id</c>: есть у хуков сабагента (<c>SubagentStart</c>, <c>SubagentStop</c>,
/// его <c>PostToolBatch</c> и <c>PermissionRequest</c>), нет у хуков главного потока.
/// </param>
/// <param name="BackgroundTasks">
/// Поле <c>background_tasks</c> у <c>Stop</c>/<c>StopFailure</c>/<c>SubagentStop</c>: живые фоновые
/// задачи сессии. <see langword="null"/> — поля в полезной нагрузке нет (старая версия Claude Code
/// или сбой разбора), пустой список — фоновых задач нет. Это разные случаи.
/// </param>
/// <remarks>
/// <paramref name="Source" /> необязателен намеренно: формат полезной нагрузки Claude Code
/// считается нестабильным (раздел 7 CLAUDE.md), и сам Claude Code помечает это поле как
/// «payloads may omit it while the field rolls out». Отсутствие значения трактуется как
/// обычная граница хода — то есть ровно как поведение до появления поля.
/// </remarks>
public sealed record HookEvent(
    HookKind Kind,
    string? SessionId,
    string? WorkingDirectory,
    string? CorrelationToken,
    DateTimeOffset ReceivedUtc,
    string? Source = null,
    string? AgentId = null,
    IReadOnlyList<BackgroundTask>? BackgroundTasks = null);
