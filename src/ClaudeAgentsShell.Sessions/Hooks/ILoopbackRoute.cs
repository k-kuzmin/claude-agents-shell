using System.Net;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Маршрут локального HTTP-слушателя, кроме самих хуков: <see cref="HookListener"/> отвечает
/// за порт и приём соединений, а разбор запроса к своему пути делает маршрут. Так новый
/// endpoint (например, <c>/mcp</c>) добавляется реализацией, а не веткой в приёмнике хуков.
/// </summary>
public interface ILoopbackRoute
{
    /// <summary>Первый сегмент пути без косых черт, например <c>mcp</c>.</summary>
    string PathSegment { get; }

    /// <summary>
    /// Обрабатывает запрос и пишет ответ. Закрывает ответ слушатель, после возврата.
    /// Исключения ввода-вывода от ушедшего клиента маршрут может не ловить — их гасит слушатель.
    /// </summary>
    /// <param name="context">Запрос и ответ.</param>
    /// <param name="cancellationToken">Остановка слушателя.</param>
    Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken);
}
