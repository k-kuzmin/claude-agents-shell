namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Строка истории сессий: то, что удалось узнать о файле транскрипта <c>.jsonl</c>.
/// Формат файла считается нестабильным, поэтому всё, кроме пути и времени, может отсутствовать.
/// </summary>
/// <param name="SessionId">Идентификатор сессии Claude Code — имя файла без расширения.</param>
/// <param name="TranscriptPath">Полный путь к <c>.jsonl</c>. Файл только читается.</param>
/// <param name="ModifiedUtc">Время последнего изменения файла.</param>
/// <param name="SizeBytes">Размер файла.</param>
/// <param name="Title">Первое сообщение пользователя; <c>null</c>, если разобрать не удалось.</param>
/// <param name="MessageCount">Число сообщений; <c>null</c>, если не считалось или разбор сорвался.</param>
/// <param name="Branch">Ветка git на момент сессии; <c>null</c>, если неизвестна.</param>
public sealed record SessionSummary(
    string SessionId,
    string TranscriptPath,
    DateTimeOffset ModifiedUtc,
    long SizeBytes,
    string? Title,
    int? MessageCount,
    string? Branch);
