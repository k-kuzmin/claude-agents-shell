using System.Windows;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Композиционный корень: единственное место, где собирается контейнер.
/// Статических синглтонов и service locator в приложении нет.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _services = null;
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddTerminalLayer();
        services.AddSessionsLayer();
        services.AddSingleton<MainWindow>();
    }
}
