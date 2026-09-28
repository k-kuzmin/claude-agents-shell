namespace ClaudeAgentsShell.App.History;

/// <summary>
/// Строка окна истории: заголовок и строка сведений «дата · ветка · короткий id».
/// Короткий id вынесен отдельно — рядом с ним кнопка копирования полного id.
/// Неизменяемая — при обновлении списка строки пересобираются, а выделение переносится
/// по <see cref="SessionId"/>.
/// </summary>
public sealed class SessionHistoryRowViewModel
{
    /// <inheritdoc cref="SessionHistoryRowViewModel" />
    /// <param name="sessionId">Идентификатор сессии Claude Code.</param>
    /// <param name="title">Имя сессии или первое сообщение одной строкой либо имя файла, если нет ни того, ни другого.</param>
    /// <param name="isTitleMissing">Заголовка нет — в <paramref name="title"/> имя файла.</param>
    /// <param name="details">Сведения перед коротким id: «дата · ветка».</param>
    /// <param name="shortId">Короткий id сессии — последний элемент строки сведений.</param>
    /// <param name="isOpen">Сессия открыта сейчас во вкладке.</param>
    /// <param name="modifiedUtc">Время изменения транскрипта — ключ сортировки.</param>
    public SessionHistoryRowViewModel(
        string sessionId,
        string title,
        bool isTitleMissing,
        string details,
        string shortId,
        bool isOpen,
        DateTimeOffset modifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(shortId);

        SessionId = sessionId;
        Title = title;
        IsTitleMissing = isTitleMissing;
        Details = details;
        ShortId = shortId;
        IsOpen = isOpen;
        ModifiedUtc = modifiedUtc;
    }

    /// <summary>Идентификатор сессии Claude Code.</summary>
    public string SessionId { get; }

    /// <summary>Имя сессии от Claude Code, иначе первое сообщение одной строкой, иначе имя файла транскрипта.</summary>
    public string Title { get; }

    /// <summary>Заголовка нет, показано имя файла (деградация по разделу 7 CLAUDE.md).</summary>
    public bool IsTitleMissing { get; }

    /// <summary>
    /// «сегодня 14:36 · main» — сведения до короткого id, без разделителя в конце.
    /// Числа сообщений нет — решение пользователя по M3.
    /// </summary>
    public string Details { get; }

    /// <summary>«0d41f2a7» — начало идентификатора; копируется всегда полный <see cref="SessionId"/>.</summary>
    public string ShortId { get; }

    /// <summary>Сессия открыта во вкладке: выбор переключит на неё, а не запустит второй <c>claude</c>.</summary>
    public bool IsOpen { get; }

    /// <summary>Время изменения транскрипта.</summary>
    public DateTimeOffset ModifiedUtc { get; }
}
