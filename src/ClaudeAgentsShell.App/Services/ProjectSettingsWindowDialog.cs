using System.Windows;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.App.Views;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Реализация <see cref="IProjectSettingsDialog"/> через модальное окно WPF.
/// Единственное место, где приложение создаёт окно настроек проекта.
/// </summary>
public sealed class ProjectSettingsWindowDialog : IProjectSettingsDialog
{
    private readonly IFolderPicker _folderPicker;
    private readonly IDirectoryProbe _directoryProbe;
    private readonly IFileProbe _fileProbe;

    /// <inheritdoc cref="ProjectSettingsWindowDialog" />
    /// <param name="folderPicker">Выбор каталога.</param>
    /// <param name="directoryProbe">Проверка существования каталога.</param>
    /// <param name="fileProbe">Наличие <c>CLAUDE.md</c> и <c>.mcp.json</c> — справочно.</param>
    public ProjectSettingsWindowDialog(
        IFolderPicker folderPicker,
        IDirectoryProbe directoryProbe,
        IFileProbe fileProbe)
    {
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(directoryProbe);
        ArgumentNullException.ThrowIfNull(fileProbe);

        _folderPicker = folderPicker;
        _directoryProbe = directoryProbe;
        _fileProbe = fileProbe;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Задача возвращается уже завершённой: модальное окно WPF крутит свой цикл сообщений
    /// внутри <see cref="Window.ShowDialog"/>, и к моменту возврата ответ пользователя известен.
    /// Асинхронная сигнатура — требование порта, а не обещание ухода в пул: звать метод
    /// можно только из потока интерфейса.
    /// </remarks>
    public Task<ProjectDefinition?> ShowAsync(ProjectDefinition project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();

        var viewModel = new ProjectSettingsViewModel(project, _folderPicker, _directoryProbe, _fileProbe);
        var window = new ProjectSettingsWindow(viewModel) { Owner = Owner() };

        // Окно ещё не показано: закрывать его до ShowDialog нельзя, иначе показ повиснет
        // на закрытом окне. Токен может сработать в потоке пула, поэтому закрытие
        // отправляется в поток окна.
        var shown = false;
        using var registration = cancellationToken.Register(() =>
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (shown)
                {
                    window.Close();
                }
            })));

        shown = true;
        var accepted = window.ShowDialog() == true;

        // Отказ — штатный исход, поэтому null, а не исключение.
        return Task.FromResult(accepted ? viewModel.Result : null);
    }

    // Владелец берётся у приложения, а не хранится ссылкой: иначе порт диалога держал бы
    // главное окно живым. Тот же приём, что у MessageBoxUserPrompt.
    private static Window Owner() =>
        System.Windows.Application.Current?.MainWindow
        ?? throw new InvalidOperationException("Главное окно ещё не создано.");
}
