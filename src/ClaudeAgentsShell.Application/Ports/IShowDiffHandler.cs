namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Аргументы инструмента <c>show_diff</c>, как их прислал агент.</summary>
/// <param name="BaseRef">База сравнения; <c>null</c> — выбрать самому.</param>
/// <param name="Directory">Каталог репозитория; <c>null</c> — текущий каталог вкладки.</param>
/// <param name="Files">Файлы, которые показать и раскрыть; пусто — все изменённые.</param>
/// <param name="Note">Пояснение для пользователя над оглавлением.</param>
public sealed record ShowDiffRequest(string? BaseRef, string? Directory, IReadOnlyList<string> Files, string? Note);

/// <summary>Чем закончился вызов <c>show_diff</c> — уходит агенту текстом результата инструмента.</summary>
public abstract record ShowDiffOutcome
{
    private ShowDiffOutcome()
    {
    }

    /// <summary>Панель открыта (или подготовлена в фоновой вкладке со значком).</summary>
    /// <param name="Summary">Кратко для агента: база, сколько файлов.</param>
    public sealed record Shown(string Summary) : ShowDiffOutcome;

    /// <summary>Токен не соответствует ни одной вкладке: сессия запущена мимо приложения или вкладку закрыли.</summary>
    public sealed record UnknownSession : ShowDiffOutcome;

    /// <summary>Diff построить нельзя.</summary>
    /// <param name="Message">Причина — агент увидит её как ошибку инструмента.</param>
    public sealed record Failed(string Message) : ShowDiffOutcome;
}

/// <summary>
/// Обработчик инструмента <c>show_diff</c>. Реализует приложение (оно знает вкладки и панель),
/// вызывает MCP-маршрут приёмника. Отвечает сразу, ответа человека не ждёт.
/// </summary>
public interface IShowDiffHandler
{
    /// <summary>Открывает diff во вкладке, которой принадлежит токен.</summary>
    /// <param name="correlationToken">Токен вкладки из заголовка запроса — тот же, что у хуков.</param>
    /// <param name="request">Аргументы инструмента.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ShowDiffOutcome> HandleAsync(string? correlationToken, ShowDiffRequest request, CancellationToken cancellationToken);
}
