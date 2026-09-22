using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Что известно об одной вкладке прямо сейчас и как очередной хук меняет её состояние
/// (раздел 5.3 ТЗ): выставленное состояние, живые сабагенты и открытые диалоги разрешения.
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
/// <b>Диалоги разрешения</b> ведутся явно, по ключу того, кто спросил: <c>agent_id</c>
/// сабагента или отдельный ключ главного потока. Вкладка «ждёт ввода», пока открыт хоть один
/// диалог; место возврата — где вкладка была бы без диалогов — общее. Диалог снимает только
/// его хозяин: <c>PostToolBatch</c> того же агента (инструмент выполнился — ответ дан),
/// <c>SubagentStop</c> того же сабагента (диалог ушёл вместе с ним), а диалог главного
/// потока — ещё и конец его хода. Промпт и граница сессии снимают все диалоги: ход сменился,
/// и поздний <c>PostToolBatch</c> не должен поднять устаревшее состояние.
/// </para>
/// </remarks>
internal sealed class TabActivity
{
    /// <summary>
    /// Ключ диалога главного потока. С настоящим <c>agent_id</c> не совпадёт: пустой
    /// <c>agent_id</c> как раз и означает главный поток.
    /// </summary>
    private const string MainThreadKey = "";

    /// <summary>Живые сабагенты по <c>agent_id</c> — откат на случай, когда <c>background_tasks</c> нет.</summary>
    private readonly HashSet<string> _liveAgents = new(StringComparer.Ordinal);

    /// <summary>Открытые диалоги разрешения по ключу того, кто спросил.</summary>
    private readonly HashSet<string> _openDialogs = new(StringComparer.Ordinal);

    /// <summary>
    /// Куда вернуть вкладку, когда закроется последний диалог. Имеет смысл, только пока
    /// <see cref="_openDialogs"/> не пуст.
    /// </summary>
    private TabState _resume;

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
    /// <c>background_tasks</c> дал бы ложное «ждёт ввода». Диалоги, наоборот, снимаются:
    /// промпт отправлен — значит, диалога на экране уже нет.
    /// </remarks>
    private TabState? OnPromptSubmitted()
    {
        _openDialogs.Clear();
        return Publish(TabState.Busy);
    }

    /// <remarks>
    /// Ход кончился — штатно (<c>Stop</c>) либо ошибкой API или отменой (<c>StopFailure</c>):
    /// на оборванном ходе Stop может не прийти вовсе. Если фоновые задачи живы, от человека
    /// сейчас ничего не нужно — сессия продолжится сама.
    /// <para>
    /// Конец хода закрывает только диалог главного потока: пока он открыт, ход кончиться
    /// не может, значит, был отказ. Диалоги сабагентов всё ещё на экране — вкладка остаётся
    /// «ждёт ввода», а итог этого Stop запоминается как место возврата.
    /// </para>
    /// </remarks>
    private TabState? OnTurnEnded(IReadOnlyList<BackgroundTask>? backgroundTasks)
    {
        var next = ResolveTurnEnd(backgroundTasks);
        var closed = _openDialogs.Remove(MainThreadKey);
        return Settle(next, closed, publishWhenNoDialogs: true);
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
    /// Открытый диалог этого сабагента уходит вместе с ним; пока открыты чужие, пробуждение
    /// откладывается до ответа на них.
    /// </remarks>
    private TabState? OnSubagentStopped(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        // Пробуждение решается от состояния «без диалогов», а не от показанного «ждёт ввода».
        var baseline = CurrentWithoutDialogs();
        var wakes = _liveAgents.Remove(agentId)
            && _liveAgents.Count == 0
            && baseline == TabState.BackgroundWork;
        var closed = _openDialogs.Remove(agentId);

        return Settle(wakes ? TabState.Busy : baseline, closed, publishWhenNoDialogs: wakes);
    }

    /// <remarks>
    /// Пачка инструментов главного потока — доказательство, что главный агент работает,
    /// как бы ни начался ход: промпт, <c>!</c>-команда (она хуков не даёт вовсе) или
    /// пробуждение по фоновой задаче. Пачка сабагента этого не доказывает и вкладку не будит.
    /// Пачка закрывает только диалог своего агента: инструмент выполнился, значит, ответ дан.
    /// Об ответе на чужой диалог она ничего не говорит.
    /// </remarks>
    private TabState? OnToolBatch(string? agentId)
    {
        if (State == TabState.Unknown)
        {
            // Сессия закончилась или процесс умер, а curl хука доехал позже.
            return null;
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            var mainClosed = _openDialogs.Remove(MainThreadKey);
            return Settle(TabState.Busy, mainClosed, publishWhenNoDialogs: true);
        }

        var baseline = CurrentWithoutDialogs();
        var closed = _openDialogs.Remove(agentId);
        return Settle(baseline, closed, publishWhenNoDialogs: false);
    }

    /// <remarks>
    /// Диалог разрешения — от главного потока или от сабагента — показывается как «ждёт
    /// ввода». Место возврата запоминает первый диалог; следующие его не трогают, иначе
    /// оно затёрлось бы самим «ждёт ввода».
    /// </remarks>
    private TabState? OnPermissionRequested(string? agentId)
    {
        if (State == TabState.Unknown)
        {
            // Сессия закончилась или процесс умер, а curl хука доехал позже.
            return null;
        }

        var key = string.IsNullOrWhiteSpace(agentId) ? MainThreadKey : agentId;

        if (_openDialogs.Count > 0)
        {
            _openDialogs.Add(key);
            return null;
        }

        _resume = State;
        _openDialogs.Add(key);
        return Publish(TabState.AwaitingInput);
    }

    /// <remarks>Сессии больше нет — её сабагентов и диалогов тоже.</remarks>
    private TabState? OnSessionEnded()
    {
        ForgetSession();
        return Publish(TabState.Unknown);
    }

    /// <summary>Где вкладка была бы, не будь открытых диалогов.</summary>
    private TabState CurrentWithoutDialogs() => _openDialogs.Count > 0 ? _resume : State;

    /// <summary>
    /// Сводит изменение с открытыми диалогами: пока открыт хоть один, вкладка остаётся
    /// «ждёт ввода», а <paramref name="next"/> становится местом возврата.
    /// </summary>
    /// <param name="next">Где вкладка была бы без диалогов.</param>
    /// <param name="dialogClosed">Этим хуком закрыт диалог — показ обязан смениться.</param>
    /// <param name="publishWhenNoDialogs">
    /// Хук меняет состояние сам по себе, даже если диалогов не было.
    /// </param>
    private TabState? Settle(TabState next, bool dialogClosed, bool publishWhenNoDialogs)
    {
        if (_openDialogs.Count > 0)
        {
            _resume = next;
            return null;
        }

        return dialogClosed || publishWhenNoDialogs ? Publish(next) : null;
    }

    /// <summary>Снимает всё, что принадлежало сессии: учёт сабагентов и открытые диалоги.</summary>
    private void ForgetSession()
    {
        _liveAgents.Clear();
        _openDialogs.Clear();
    }

    /// <summary>Запоминает состояние как выставленное и возвращает его для показа.</summary>
    private TabState Publish(TabState state)
    {
        State = state;
        return state;
    }
}
