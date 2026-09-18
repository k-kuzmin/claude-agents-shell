namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Разбор поля <c>source</c> хука <c>SessionStart</c>: настоящая ли это граница хода.
/// </summary>
/// <remarks>
/// Живёт отдельно от координатора, чтобы его <c>switch</c> оставался про переходы состояний,
/// а знание протокола Claude Code — здесь. Формат считается нестабильным (раздел 7 CLAUDE.md),
/// поэтому правило записано как список известных исключений, а не как белый список: незнакомое
/// и отсутствующее значение ведут себя как граница хода — ровно как до появления поля.
/// </remarks>
internal static class HookSourceRules
{
    /// <summary>
    /// Сжатие контекста: сессия перезапускает не ход, а собственное окно контекста —
    /// и продолжает работать.
    /// </summary>
    private const string CompactSource = "compact";

    /// <summary>
    /// Начинает ли <c>SessionStart</c> с таким <c>source</c> новый ход.
    /// </summary>
    /// <param name="source">Значение поля <c>source</c> или <c>null</c>, если его не было.</param>
    /// <returns>
    /// <c>false</c> только для сжатия контекста; для всего остального — <c>true</c>.
    /// </returns>
    /// <remarks>
    /// Claude Code присылает здесь <c>startup</c>, <c>resume</c>, <c>clear</c>, <c>compact</c>
    /// или <c>fork</c>. Границей хода не является ровно одно значение — <c>compact</c>:
    /// автосжатие срабатывает **посреди хода**, и выставить на нём «простаивает» значило бы
    /// показать серую точку, пока агент работает, а обнулить счётчик сабагентов — потерять
    /// уже запущенных.
    /// <para>
    /// <c>fork</c> границей считается: по описанию полей <c>seconds_since_last_response</c>,
    /// <c>context_tokens</c> и <c>prompt_cache_likely_expired</c> он идёт в одной группе
    /// с <c>resume</c> («resume/fork: …»), то есть поднимает сохранённый транскрипт.
    /// Живых сабагентов у такой сессии нет, а её первое состояние — «простаивает».
    /// </para>
    /// <para>
    /// <c>startup</c>, <c>resume</c> и <c>clear</c> — тем более границы: у первых двух процесс
    /// сессии только что поднялся, у третьего контекст очищен, и прежний ход продолжаться
    /// не может.
    /// </para>
    /// </remarks>
    public static bool StartsNewTurn(string? source) =>
        !string.Equals(source, CompactSource, StringComparison.Ordinal);
}
