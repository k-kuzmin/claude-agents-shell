namespace ClaudeAgentsShell.App.History;

/// <summary>
/// Строка окна истории: заголовок и строка сведений «дата · ветка · короткий id».
/// Неизменяемая — при обновлении списка строки пересобираются, а выделение переносится
/// по <see cref="ProjectId"/> и <see cref="SessionId"/>.
/// </summary>
public sealed class SessionHistoryRowViewModel
{
    /// <inheritdoc cref="SessionHistoryRowViewModel" />
    /// <param name="projectId">Проект сессии.</param>
    /// <param name="sessionId">Идентификатор сессии Claude Code.</param>
    /// <param name="title">Первое сообщение одной строкой либо имя файла, если заголовка нет.</param>
    /// <param name="isTitleMissing">Заголовка нет — в <paramref name="title"/> имя файла.</param>
    /// <param name="details">Строка сведений под заголовком.</param>
    /// <param name="isOpen">Сессия открыта сейчас во вкладке.</param>
    /// <param name="modifiedUtc">Время изменения транскрипта — ключ сортировки.</param>
    public SessionHistoryRowViewModel(
        Guid projectId,
        string sessionId,
        string title,
        bool isTitleMissing,
        string details,
        bool isOpen,
        DateTimeOffset modifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(details);

        ProjectId = projectId;
        SessionId = sessionId;
        Title = title;
        IsTitleMissing = isTitleMissing;
        Details = details;
        IsOpen = isOpen;
        ModifiedUtc = modifiedUtc;
    }

    /// <summary>Проект сессии.</summary>
    public Guid ProjectId { get; }

    /// <summary>Идентификатор сессии Claude Code.</summary>
    public string SessionId { get; }

    /// <summary>Первое сообщение одной строкой либо имя файла транскрипта.</summary>
    public string Title { get; }

    /// <summary>Заголовка нет, показано имя файла (деградация по разделу 7 CLAUDE.md).</summary>
    public bool IsTitleMissing { get; }

    /// <summary>
    /// «сегодня 14:36 · main · 0d41f2a7»; в режиме «все проекты» впереди имя проекта.
    /// Числа сообщений нет — решение пользователя по M3.
    /// </summary>
    public string Details { get; }

    /// <summary>Сессия открыта во вкладке: выбор переключит на неё, а не запустит второй <c>claude</c>.</summary>
    public bool IsOpen { get; }

    /// <summary>Время изменения транскрипта.</summary>
    public DateTimeOffset ModifiedUtc { get; }
}
