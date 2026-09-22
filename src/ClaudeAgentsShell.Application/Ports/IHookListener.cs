using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Событие хука Claude Code.</summary>
public sealed class HookEventArgs(HookEvent hookEvent) : EventArgs
{
    /// <summary>Разобранное событие.</summary>
    public HookEvent Event { get; } = hookEvent;
}

/// <summary>
/// Локальный приёмник хуков Claude Code: слушает <c>http://127.0.0.1:&lt;порт&gt;/hook</c>
/// только на loopback, порт выбирается свободный при старте.
/// Это единственный источник состояния вкладки — вывод агента не разбирается.
/// </summary>
public interface IHookListener : IAsyncDisposable
{
    /// <summary>Адрес, который прописывается в сгенерированный файл настроек хуков.</summary>
    Uri Endpoint { get; }

    /// <summary>
    /// Адрес MCP-маршрута на том же слушателе (<c>http://127.0.0.1:&lt;порт&gt;/mcp</c>) —
    /// прописывается в сгенерированный файл для <c>--mcp-config</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Приёмник не запущен или MCP-маршрут не зарегистрирован: сессия запускается без MCP.
    /// </exception>
    Uri McpEndpoint { get; }

    /// <summary>Пришло событие хука.</summary>
    event EventHandler<HookEventArgs>? HookReceived;

    /// <summary>Поднимает слушателя и фиксирует <see cref="Endpoint"/>.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}
