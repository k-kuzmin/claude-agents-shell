using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Git;
using ClaudeAgentsShell.Sessions.History;
using ClaudeAgentsShell.Sessions.Hooks;
using ClaudeAgentsShell.Sessions.Launch;
using ClaudeAgentsShell.Sessions.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeAgentsShell.Sessions;

/// <summary>Регистрация слоя сессий: список проектов, ветка git, команды запуска.</summary>
public static class SessionsServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует пути приложения, хранилище проектов, проверку каталогов и файлов,
    /// журнал сбоев, чтение и слежение за веткой, сборку команд запуска, приёмник хуков,
    /// открытие ссылок, журнал принятых хуков,
    /// генератор настроек с хуками, чтение истории сессий и запуск проводника.
    /// </summary>
    public static IServiceCollection AddSessionsLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new SessionsOptions());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IAppDataPaths>(static _ => AppDataPaths.ForCurrentUser());
        services.TryAddSingleton<IProjectStore, ProjectStore>();
        services.TryAddSingleton<ILayoutStore, JsonLayoutStore>();
        services.TryAddSingleton<IDirectoryProbe, DirectoryProbe>();
        services.TryAddSingleton<IFileProbe, FileProbe>();
        services.TryAddSingleton<IShellLauncher, ShellLauncher>();
        services.TryAddSingleton<ICrashLog, CrashLog>();
        services.TryAddSingleton<IUrlLauncher, UrlLauncher>();

        services.TryAddSingleton<IGitBranchReader, GitBranchReader>();
        services.TryAddSingleton<IGitBranchWatcher, GitBranchWatcher>();

        services.TryAddSingleton<ISessionCommandBuilder, SessionCommandBuilder>();

        services.TryAddSingleton<IHookLog, HookLog>();
        services.TryAddSingleton<IHookListener, HookListener>();
        services.TryAddSingleton<IHookSettingsProvider, HookSettingsProvider>();
        services.TryAddSingleton<ISessionHistoryReader, SessionHistoryReader>();

        return services;
    }
}
