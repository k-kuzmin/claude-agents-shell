using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Terminal.Protocol;
using ClaudeAgentsShell.Terminal.Pty;
using ClaudeAgentsShell.Terminal.Shells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeAgentsShell.Terminal;

/// <summary>Регистрация слоя терминала. Композиция DI живёт в одном месте — в приложении.</summary>
public static class TerminalServiceCollectionExtensions
{
    /// <summary>Регистрирует ConPTY, протокол моста и склейку вывода.</summary>
    public static IServiceCollection AddTerminalLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new TerminalOptions());
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IBridgeMessageWriter, BridgeMessageWriter>();
        services.AddSingleton<IBridgeMessageParser, BridgeMessageParser>();

        // Порядок регистрации задаёт порядок отката резолвера: pwsh → powershell.exe → cmd.exe.
        services.AddSingleton<IShellProvider, PwshShellProvider>();
        services.AddSingleton<IShellProvider, WindowsPowerShellProvider>();
        services.AddSingleton<IShellProvider, CmdShellProvider>();
        services.AddSingleton<IShellResolver, ShellResolver>();

        services.AddSingleton<IPtySessionFactory, ConPtySessionFactory>();
        services.AddSingleton<TerminalWorkspace>();

        return services;
    }
}
