using System.Windows;
using ClaudeAgentsShell.Application.Ports;
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
        if (_services is { } services)
        {
            // К этому моменту окно уже освободило мост и псевдоконсоли; повторное освобождение
            // идемпотентно и не уходит в ожидание.
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _services = null;
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddTerminalLayer();
        services.AddSessionsLayer();

        // Мост — единственное место, где приложение знает про WebView2.
        services.AddSingleton<WebView2TerminalBridge>();
        services.AddSingleton<ITerminalBridge>(static sp => sp.GetRequiredService<WebView2TerminalBridge>());

        services.AddSingleton<MainWindow>();
    }
}
