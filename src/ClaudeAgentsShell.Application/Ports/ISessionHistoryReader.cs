using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Читает историю сессий Claude Code из <c>~/.claude/projects/&lt;slug&gt;</c>.
/// Файлы только читаются: ни записи, ни удаления. Любой сбой разбора деградирует
/// до «имя файла и дата» и не роняет приложение.
/// </summary>
/// <remarks>
/// Сводка несёт и первое сообщение (<see cref="SessionSummary.Title"/>), и имя, которое сессии
/// дал сам Claude Code (<see cref="SessionSummary.Name"/>). Глубина чтения ограничена у обоих:
/// заголовок ищется с начала файла до своего предела, имя — в хвосте фиксированного размера.
/// Деградация до «имя файла и дата» разрешена разделом 7 CLAUDE.md — формат <c>.jsonl</c>
/// считается нестабильным.
/// </remarks>
public interface ISessionHistoryReader
{
    /// <summary>
    /// Сводки по сессиям рабочего каталога, от свежих к старым.
    /// Каталога нет — пустой список, это не ошибка.
    /// </summary>
    /// <remarks>
    /// Только сессии, которые человек вёл в терминале: вспомогательные — файлы <c>agent-*.jsonl</c>,
    /// запуски через SDK и <c>claude -p</c> (<c>entrypoint: sdk-*</c>), первое <c>isSidechain: true</c> —
    /// в список не входят. Признак не удалось прочитать — сессия остаётся. <see cref="ReadOneAsync"/>
    /// и <see cref="ReadTranscriptAsync"/> так не фильтруют: хук приходит по уже известной сессии.
    /// </remarks>
    Task<IReadOnlyList<SessionSummary>> ReadAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Сводка по одной известной сессии; транскрипт ищется в каталоге, собранном из рабочего
    /// каталога. Нужна вкладке, когда хук принёс <c>session_id</c>, но не <c>transcript_path</c>
    /// (или принёс негодный).
    /// </summary>
    /// <returns>
    /// <c>null</c> — файла ещё нет или его не удалось открыть: спросить позже имеет смысл.
    /// Сводка без <see cref="SessionSummary.DisplayTitle"/> — файл прочитан, но ни имени, ни
    /// первого сообщения в нём пока нет. Спросить позже тоже имеет смысл: транскрипт живой сессии
    /// растёт, имя в нём появляется по ходу первого хода, а меняется в любой момент (<c>/rename</c>).
    /// </returns>
    /// <remarks>
    /// Повторный вопрос по неизменившемуся файлу бесплатен (кэш), по дописанному — стоит
    /// просмотра дописанного, а не всего файла.
    /// </remarks>
    Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// То же, что <see cref="ReadOneAsync"/>, но по пути из поля <c>transcript_path</c> хука.
    /// Точнее: каталог сессии мог смениться после <c>cd</c> или перехода в worktree, а путь из
    /// хука указывает на файл, который Claude Code действительно пишет.
    /// </summary>
    /// <param name="transcriptPath">Путь из полезной нагрузки хука — пришёл снаружи.</param>
    /// <param name="sessionId">Сессия, которой путь принадлежит.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>
    /// Как у <see cref="ReadOneAsync"/>; кроме того <c>null</c>, если путь не годится: не
    /// полный, ведёт за пределы <c>~/.claude/projects</c> или называет не <c>&lt;sessionId&gt;.jsonl</c>.
    /// Тогда вызывающий откатывается к <see cref="ReadOneAsync"/>.
    /// </returns>
    Task<SessionSummary?> ReadTranscriptAsync(string transcriptPath, string sessionId, CancellationToken cancellationToken);
}
