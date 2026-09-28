using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Один файл из аргументов <c>show_file</c>.</summary>
/// <param name="Path">Путь относительно каталога или абсолютный внутри него.</param>
/// <param name="Focus">Строки, к которым прокрутить; <c>null</c> — весь файл с начала.</param>
public sealed record ShowFileItem(string Path, LineRange? Focus);

/// <summary>Аргументы инструмента <c>show_file</c>, как их прислал агент.</summary>
/// <param name="Directory">Каталог, от которого считаются пути; <c>null</c> — текущий каталог вкладки.</param>
/// <param name="Files">Файлы; не пусто — пустой список отсекает разбор аргументов.</param>
/// <param name="Note">Пояснение для пользователя над списком.</param>
public sealed record ShowFileRequest(string? Directory, IReadOnlyList<ShowFileItem> Files, string? Note);

/// <summary>Чем закончился вызов <c>show_file</c> — уходит агенту текстом результата инструмента.</summary>
public abstract record ShowFileOutcome
{
    private ShowFileOutcome()
    {
    }

    /// <summary>Панель открыта (или подготовлена в фоновой вкладке со значком).</summary>
    /// <param name="Summary">Кратко для агента: что показано, какие файлы не удалось и почему.</param>
    public sealed record Shown(string Summary) : ShowFileOutcome;

    /// <summary>Токен не соответствует ни одной вкладке.</summary>
    public sealed record UnknownSession : ShowFileOutcome;

    /// <summary>Показать нельзя ничего: каталога нет, ни один файл не прочитан.</summary>
    /// <param name="Message">Причина — агент увидит её как ошибку инструмента.</param>
    public sealed record Failed(string Message) : ShowFileOutcome;
}

/// <summary>
/// Обработчик инструмента <c>show_file</c>. Реализует приложение, вызывает MCP-маршрут приёмника.
/// Отвечает сразу после отправки на страницу, ответа человека не ждёт.
/// </summary>
public interface IShowFileHandler
{
    /// <summary>Показывает файлы во вкладке, которой принадлежит токен.</summary>
    /// <param name="correlationToken">Токен вкладки из заголовка запроса — тот же, что у хуков.</param>
    /// <param name="request">Аргументы инструмента.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ShowFileOutcome> HandleAsync(string? correlationToken, ShowFileRequest request, CancellationToken cancellationToken);
}
