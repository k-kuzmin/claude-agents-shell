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
}
