using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.Sessions;

/// <summary>Регистрация слоя сессий: список проектов, история, приёмник хуков.</summary>
public static class SessionsServiceCollectionExtensions
{
    /// <summary>Регистрирует хранилище проектов, чтение истории и приёмник хуков.</summary>
    public static IServiceCollection AddSessionsLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
