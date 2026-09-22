using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Что известно об одной вкладке прямо сейчас и как очередной хук меняет её состояние
/// (раздел 5.3 ТЗ): выставленное состояние, живые сабагенты и открытый диалог разрешения.
/// </summary>
/// <remarks>
/// Отделён от <see cref="SessionStateCoordinator"/>, чтобы тот оставался склейкой портов —
/// токен, диспетчер, заголовок, — а переходы состояний жили в одном месте и проверялись
/// отдельно от них. Экземпляр принадлежит координатору и трогается только из потока
/// интерфейса, поэтому блокировок нет.
/// <para>
/// <b>Конец хода.</b> Решает поле <c>background_tasks</c> у <c>Stop</c>/<c>StopFailure</c>:
/// живая фоновая задача любого типа — «занята своей работой», пустой список — «ждёт ввода».
/// Только если поля нет (старая версия Claude Code или сбой разбора), решает собственный учёт
/// живых сабагентов по <c>agent_id</c>.
/// </para>
/// <para>
/// <b>Учёт сабагентов</b> — множество, а не счётчик: задвоенный <c>SubagentStart</c> или
/// <c>SubagentStop</c> его не сдвигает, а остановка незнакомого сабагента ничего не снимает.
/// Потерянный <c>SubagentStop</c> всё ещё держит запись, но живёт она только до первого
/// пустого <c>background_tasks</c> (Claude Code сам сказал, что фоновых задач нет) или до
/// границы сессии — <c>SessionStart</c> с настоящей сменой хода и <c>SessionEnd</c>.
/// На сжатии контекста и на отправке промпта учёт не очищается: они случаются посреди
/// фоновой работы. Хук сабагента без <c>agent_id</c> в учёт не попадает — сопоставить его
/// запуск с остановкой нечем.
/// </para>
/// <para>
/// <b>Диалог разрешения</b> ведётся явным признаком, а не угадывается по состоянию:
/// <c>PermissionRequest</c> запоминает, где вкладка была без диалога, и выставляет «ждёт
/// ввода»; любой следующий <c>PostToolBatch</c> — инструмент уже выполнился, значит, ответ
/// дан — снимает признак. Всё, что меняет ход (промпт, граница сессии, конец хода главного
/// потока), снимает его тоже, чтобы поздний <c>PostToolBatch</c> не поднял устаревшее
/// состояние.
/// </para>
/// </remarks>
internal sealed class TabActivity
{
    /// <summary>Живые сабагенты по <c>agent_id</c> — откат на случай, когда <c>background_tasks</c> нет.</summary>
    private readonly HashSet<string> _liveAgents = new(StringComparer.Ordinal);

    /// <summary>Открытый диалог разрешения; <see langword="null"/> — диалога нет.</summary>
    private PendingPermission? _permission;

    /// <summary>Состояние, выставленное вкладке последним; до первого хука — <c>Unknown</c>.</summary>
    public TabState State { get; private set; } = TabState.Unknown;

    /// <summary>
    /// Применяет хук к вкладке.
    /// </summary>
    /// <param name="hookEvent">Событие хука этой вкладки.</param>
    /// <returns>
    /// Состояние, которое надо показать, или <see langword="null"/>, если показ не меняется.
    /// Повтор того же состояния тоже возвращается — полоса вкладок решает сама, перерисовывать ли.
    /// </returns>
    public TabState? Apply(HookEvent hookEvent)
    {
        ArgumentNullException.ThrowIfNull(hookEvent);

        return hookEvent.Kind switch
        {
            HookKind.SessionStart => OnSessionStart(hookEvent.Source),
            HookKind.UserPromptSubmit => OnPromptSubmitted(),
            HookKind.Stop or HookKind.StopFailure => OnTurnEnded(hookEvent.BackgroundTasks),
            HookKind.SubagentStart => OnSubagentStarted(hookEvent.AgentId),
            HookKind.SubagentStop => OnSubagentStopped(hookEvent.AgentId),
            HookKind.PostToolBatch => OnToolBatch(hookEvent.AgentId),
            HookKind.PermissionRequest => OnPermissionRequested(hookEvent.AgentId),
            HookKind.SessionEnd => OnSessionEnded(),

            // Незарегистрированный хук игнорируется: состояние вкладки не меняется.
            _ => null,
        };
    }

    /// <remarks>
    /// Сжатие контекста приходит тем же хуком, но ходом не является: сессия в этот момент
    /// работает. Выставить «простаивает» значило бы зажечь серую точку посреди работы агента,
    /// а очистить учёт — забыть уже запущенных сабагентов. Разбор source — в
    /// <see cref="HookSourceRules"/>.
    /// </remarks>
    private TabState? OnSessionStart(string? source)
    {
        if (!HookSourceRules.StartsNewTurn(source))
        {
            return null;
        }

        // Настоящее начало сессии: незакрытые сабагенты и диалоги прежней не считаются.
        ForgetSession();
        return Publish(TabState.Idle);
    }

    /// <remarks>
    /// Учёт сабагентов здесь **не** очищается, хотя ход и начинается. Промпт прилетает
    /// и посреди фоновой работы: человек дописывает его прямо из «занята своей работой»,
    /// а Claude Code шлёт этот хук ещё и сам — по /loop, по расписанию и на машинные
    /// сообщения. Очистка стёрла бы живых сабагентов, и следующий Stop без
    /// <c>background_tasks</c> дал бы ложное «ждёт ввода».
    /// </remarks>
    private TabState? OnPromptSubmitted()
    {
        _permission = null;
        return Publish(TabState.Busy);
    }

    /// <remarks>
    /// Ход кончился — штатно (<c>Stop</c>) либо ошибкой API или отменой (<c>StopFailure</c>):
    /// на оборванном ходе Stop может не прийти вовсе. Если фоновые задачи живы, от человека
    /// сейчас ничего не нужно — сессия продолжится сама.
    /// <para>
    /// Диалог сабагента ход главного потока не закрывает: он всё ещё на экране, поэтому
    /// вкладка остаётся «ждёт ввода», а итог этого Stop запоминается как место возврата.
    /// Диалог главного потока, наоборот, закрыт — иначе ход бы не кончился.
    /// </para>
    /// </remarks>
    private TabState? OnTurnEnded(IReadOnlyList<BackgroundTask>? backgroundTasks)
    {
        var next = ResolveTurnEnd(backgroundTasks);

        if (_permission is { FromMainThread: false } permission)
        {
            _permission = permission with { Resume = next };
            return null;
        }

        _permission = null;
        return Publish(next);
    }

    /// <summary>Куда уходит вкладка в конце хода — см. раздел «Конец хода» в описании класса.</summary>
    private TabState ResolveTurnEnd(IReadOnlyList<BackgroundTask>? backgroundTasks)
    {
        if (backgroundTasks is null)
        {
            return _liveAgents.Count > 0 ? TabState.BackgroundWork : TabState.AwaitingInput;
        }

        if (backgroundTasks.Count > 0)
        {
            // Учёт по списку не пересобирается: совпадает ли id задачи с agent_id,
            // не проверено, а ошибка в сверке снимала бы живых сабагентов.
            return TabState.BackgroundWork;
        }

        // Claude Code сам сказал, что фоновых задач нет: потерянный SubagentStop больше
        // не держит вкладку.
        _liveAgents.Clear();
        return TabState.AwaitingInput;
    }

    /// <remarks>
    /// Только учёт: сабагента запускает работающий агент, маркер уже стоит верный.
    /// </remarks>
    private TabState? OnSubagentStarted(string? agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            _liveAgents.Add(agentId);
        }

        return null;
    }

    /// <remarks>
    /// Последний живой сабагент закончил — главный агент вот-вот продолжит сам. Будит вкладку
    /// только остановка сабагента, который действительно был в учёте, и только из «занята
    /// своей работой»: задвоенный или незнакомый SubagentStop иначе сбил бы «ждёт ввода»,
    /// которого ждёт пользователь, или разбудил бы вкладку, которую держит фоновая команда.
    /// При открытом диалоге пробуждение откладывается до ответа на него.
    /// </remarks>
    private TabState? OnSubagentStopped(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || !_liveAgents.Remove(agentId) || _liveAgents.Count > 0)
        {
            return null;
        }

        if (_permission is { } permission)
        {
            if (permission.Resume == TabState.BackgroundWork)
            {
                _permission = permission with { Resume = TabState.Busy };
            }

            return null;
        }

        return State == TabState.BackgroundWork ? Publish(TabState.Busy) : null;
    }

    /// <remarks>
    /// Пачка инструментов главного потока — доказательство, что главный агент работает,
    /// как бы ни начался ход: промпт, <c>!</c>-команда (она хуков не даёт вовсе) или
    /// пробуждение по фоновой задаче. Пачка сабагента этого не доказывает и вкладку не будит.
    /// Любая пачка закрывает диалог разрешения: инструмент выполнился, значит, ответ дан.
    /// </remarks>
    private TabState? OnToolBatch(string? agentId)
    {
        if (State == TabState.Unknown)
        {
            // Сессия закончилась или процесс умер, а curl хука доехал позже.
            return null;
        }

        var permission = _permission;
        _permission = null;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Publish(TabState.Busy);
        }

        return permission is { } pending ? Publish(pending.Resume) : null;
    }

    /// <remarks>
    /// Диалог разрешения — от главного потока или от сабагента — показывается как «ждёт
    /// ввода». Повторный запрос, пока диалог открыт, место возврата не трогает: иначе оно
    /// затёрлось бы самим «ждёт ввода».
    /// </remarks>
    private TabState? OnPermissionRequested(string? agentId)
    {
        if (State == TabState.Unknown)
        {
            // Сессия закончилась или процесс умер, а curl хука доехал позже.
            return null;
        }

        var fromMainThread = string.IsNullOrWhiteSpace(agentId);

        if (_permission is { } permission)
        {
            _permission = permission with { FromMainThread = permission.FromMainThread || fromMainThread };
            return null;
        }

        _permission = new PendingPermission(fromMainThread, State);
        return Publish(TabState.AwaitingInput);
    }

    /// <remarks>Сессии больше нет — её сабагентов и диалогов тоже.</remarks>
    private TabState? OnSessionEnded()
    {
        ForgetSession();
        return Publish(TabState.Unknown);
    }

    /// <summary>Снимает всё, что принадлежало сессии: учёт сабагентов и открытый диалог.</summary>
    private void ForgetSession()
    {
        _liveAgents.Clear();
        _permission = null;
    }

    /// <summary>Запоминает состояние как выставленное и возвращает его для показа.</summary>
    private TabState Publish(TabState state)
    {
        State = state;
        return state;
    }

    /// <summary>Открытый диалог разрешения.</summary>
    /// <param name="FromMainThread">
    /// Диалог просил главный поток. Такой диалог закрывается концом хода: пока он открыт,
    /// ход кончиться не может.
    /// </param>
    /// <param name="Resume">Куда вернуть вкладку после ответа, если ответ дал сабагент.</param>
    private readonly record struct PendingPermission(bool FromMainThread, TabState Resume);
}
