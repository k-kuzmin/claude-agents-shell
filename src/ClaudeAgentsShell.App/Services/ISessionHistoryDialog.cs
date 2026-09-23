namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Окно истории сессий (раздел 6.4 ТЗ): модальное, над главным окном. Поиск по первому
/// сообщению, строки «первое сообщение / дата, ветка, короткий id», стрелки, <c>Enter</c>, <c>Esc</c>.
/// Отступления по решению пользователя: окно показывает историю одного проекта — того, с чьей
/// строки его открыли, фильтра по проектам нет; число сообщений не показывается.
/// </summary>
/// <remarks>
/// Окно только выбирает сессию. Что делать с выбором — переключить на уже открытую вкладку
/// или открыть новую с <c>--resume</c> — решает вызывающий.
/// </remarks>
public interface ISessionHistoryDialog
{
    /// <summary>Показывает окно и ждёт его закрытия.</summary>
    /// <param name="request">Проект, чья история показывается, и уже открытые сессии.</param>
    /// <param name="cancellationToken">Отмена ожидания.</param>
    /// <returns>Выбранная сессия либо <c>null</c>, если окно закрыли без выбора (штатный исход).</returns>
    Task<SessionHistoryChoice?> ShowAsync(SessionHistoryRequest request, CancellationToken cancellationToken);
}

/// <summary>Что показать в окне истории.</summary>
/// <param name="Project">Проект строки, с которой открыли окно; показывается только его история.</param>
/// <param name="OpenSessionIds">
/// Идентификаторы сессий, открытых сейчас во вкладках. Такие строки помечены «открыта»;
/// выбор их не запускает второй <c>claude</c> на тот же транскрипт — вызывающий переключает вкладку.
/// </param>
public sealed record SessionHistoryRequest(
    SessionHistoryProject Project,
    IReadOnlySet<string> OpenSessionIds);

/// <summary>Проект, чья история показывается в окне.</summary>
/// <param name="Id">Идентификатор проекта.</param>
/// <param name="Name">Отображаемое имя — для заголовка окна.</param>
/// <param name="WorkingDirectory">Каталог проекта — по нему ищется история.</param>
public sealed record SessionHistoryProject(Guid Id, string Name, string WorkingDirectory);

/// <summary>Выбор в окне истории.</summary>
/// <param name="ProjectId">Проект выбранной сессии.</param>
/// <param name="SessionId">Идентификатор сессии Claude Code.</param>
public sealed record SessionHistoryChoice(Guid ProjectId, string SessionId);
