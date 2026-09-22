using ClaudeAgentsShell.App.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Композиционный корень: единственное место, где собирается контейнер.
/// Статических синглтонов и service locator в приложении нет.
/// </summary>
/// <remarks>
/// Вынесен из <see cref="App"/> отдельным классом, чтобы список регистраций мог проверить
/// тест: порт, добавленный в конструктор без регистрации, компилируется и не роняет ни один
/// тест, зато роняет приложение на старте. Класс внутренний — наружу сборки состав
/// контейнера не выставляется.
/// </remarks>
internal static class AppComposition
{
    /// <summary>
    /// Проверки, с которыми строится провайдер. Живут рядом с регистрациями, чтобы тест
    /// проверял граф ровно так же, как приложение: копия литералов разъехалась бы молча,
    /// как разъехалась бы копия списка регистраций. Каждое обращение отдаёт свой экземпляр —
    /// тип изменяемый, и общий на всех был бы разделяемым состоянием.
    /// </summary>
    public static ServiceProviderOptions ProviderOptions => new()
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    };

    /// <summary>Регистрирует всё, что нужно главному окну: слои терминала и сессий, порты оболочки и ViewModel.</summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTerminalLayer();
        services.AddSessionsLayer();

        // Мост — единственное место, где приложение знает про WebView2.
        services.AddSingleton<WebView2TerminalBridge>();
        services.AddSingleton<ITerminalBridge>(static sp => sp.GetRequiredService<WebView2TerminalBridge>());

        // Порты уровня оболочки: всё, что ViewModel нужно от WPF и файловой системы.
        // В самих ViewModel нет ни File.*, ни Process.*, ни Dispatcher.
        services.AddSingleton<IFolderPicker, OpenFolderDialogPicker>();
        services.AddSingleton<IUserPrompt, DialogUserPrompt>();
        services.AddSingleton<IWebView2MissingDialog, WebView2MissingDialog>();
        services.AddSingleton<IShellAvailability, ShellAvailabilityProbe>();
        services.AddSingleton<IProjectSettingsDialog, ProjectSettingsWindowDialog>();

        // Глобальные обработчики исключений; ICrashLog регистрирует слой Sessions.
        // Признак гашения общий на приложение: его взводит окно, а читает докладчик о сбоях.
        services.AddSingleton<ShutdownSignal>();
        services.AddSingleton<CrashReporter>();
        // IDirectoryProbe регистрирует слой Sessions: Directory.* — файловая система,
        // а не WPF-специфика, и сборке оболочки не место её трогать.

        // Захватывает Dispatcher.CurrentDispatcher, поэтому контейнер строится в потоке UI.
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();

        // Единственный источник состояния вкладок — хуки Claude Code (раздел 5.3 ТЗ).
        services.AddSingleton<SessionStateCoordinator>();

        services.AddSingleton<ProjectListViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellShortcutHandler>();

        services.AddSingleton<MainWindow>();
    }
}
