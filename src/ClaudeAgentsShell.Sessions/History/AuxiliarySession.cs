namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Отличает от сессий пользователя служебные транскрипты: сабагентов и запусков без терминала.
/// Открыть такую строку истории значит отдать её id в <c>--resume</c> и продолжить чужую ветку,
/// поэтому в список истории они не попадают.
/// </summary>
/// <remarks>
/// Как Claude Code их хранит (замер по <c>~/.claude/projects</c> пользователя):
/// <list type="bullet">
/// <item>сабагенты инструмента Agent лежат в <c>&lt;session&gt;/subagents/agent-*.jsonl</c>, в
/// подкаталоге, и перечисление каталога проекта их не видит;</item>
/// <item>старые версии клали их рядом с сессией как <c>agent-*.jsonl</c> — отсекается по имени,
/// без открытия файла;</item>
/// <item>сессии, запущенные через Agent SDK или <c>claude -p</c> (боты ревью и т.п.), лежат рядом
/// с сессиями пользователя под обычным uuid и отличаются только полем <c>entrypoint</c> вида
/// <c>sdk-*</c>; у интерактивного запуска оно <c>cli</c>;</item>
/// <item>транскрипт сабагента помечен <c>isSidechain: true</c> с первой же записи.</item>
/// </list>
/// Признак исключает только при явном свидетельстве: поле пропало из формата или не нашлось
/// до заголовка — сессия остаётся в списке.
/// </remarks>
internal static class AuxiliarySession
{
    private const string AgentFilePrefix = "agent-";
    private const string SdkEntrypointPrefix = "sdk-";

    /// <summary>Файл сабагента в старой раскладке, распознаётся по имени.</summary>
    public static bool IsAgentFileName(string fileName) =>
        fileName.StartsWith(AgentFilePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Решение по первым значениям полей, встреченным в транскрипте.</summary>
    /// <param name="entrypoint">Первое встреченное <c>entrypoint</c>, если было.</param>
    /// <param name="sidechain">Первое встреченное <c>isSidechain</c>, если было.</param>
    public static bool IsAuxiliary(string? entrypoint, bool? sidechain) =>
        sidechain == true
        || (entrypoint is not null && entrypoint.StartsWith(SdkEntrypointPrefix, StringComparison.OrdinalIgnoreCase));
}
