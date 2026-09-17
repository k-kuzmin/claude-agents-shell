using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Читает историю сессий Claude Code из <c>~/.claude/projects/&lt;slug&gt;</c>.
/// Файлы только читаются: ни записи, ни удаления. Любой сбой разбора деградирует
/// до «имя файла и дата» и не роняет приложение.
/// </summary>
public interface ISessionHistoryReader
{
    /// <summary>
    /// Сводки по сессиям рабочего каталога, от свежих к старым.
    /// Каталога нет — пустой список, это не ошибка.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ReadAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Сводка по одной известной сессии. Нужна, когда хук <c>SessionStart</c> уже принёс
    /// <c>session_id</c> и вкладке требуется заголовок из первого сообщения пользователя.
    /// Файла нет или разобрать не удалось — <c>null</c>, это не ошибка.
    /// </summary>
    Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken);
}
