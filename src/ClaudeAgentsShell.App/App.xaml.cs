using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Время жизни приложения: строит контейнер по <see cref="AppComposition"/>, показывает
/// главное окно и детерминированно освобождает контейнер на выходе.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        AppComposition.ConfigureServices(services);
        _services = services.BuildServiceProvider(AppComposition.ProviderOptions);

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
}
