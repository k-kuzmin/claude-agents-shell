using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.Terminal;

/// <summary>Регистрация слоя терминала. Композиция DI живёт в одном месте — в приложении.</summary>
public static class TerminalServiceCollectionExtensions
{
    /// <summary>Регистрирует ConPTY, протокол моста и склейку вывода.</summary>
    public static IServiceCollection AddTerminalLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new TerminalOptions());
        return services;
    }
}
