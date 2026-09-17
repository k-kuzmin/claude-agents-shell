using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Git;
using ClaudeAgentsShell.Sessions.Launch;
using ClaudeAgentsShell.Sessions.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeAgentsShell.Sessions;

/// <summary>Регистрация слоя сессий: список проектов, ветка git, команды запуска.</summary>
public static class SessionsServiceCollectionExtensions
{
    /// <summary>Регистрирует пути приложения, хранилище проектов, чтение и слежение за веткой.</summary>
    public static IServiceCollection AddSessionsLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new SessionsOptions());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IAppDataPaths>(static _ => AppDataPaths.ForCurrentUser());
        services.TryAddSingleton<IProjectStore, ProjectStore>();

        services.TryAddSingleton<IGitBranchReader, GitBranchReader>();
        services.TryAddSingleton<IGitBranchWatcher, GitBranchWatcher>();

        services.TryAddSingleton<ISessionCommandBuilder, SessionCommandBuilder>();

        return services;
    }
}
