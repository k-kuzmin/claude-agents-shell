using System.Windows.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.Input;

/// <summary>
/// Приём клавиатуры окна: распознаёт оконные сочетания и отдаёт их ViewModel.
/// Всё, что не распознано, остаётся необработанным и уходит в терминал без изменений —
/// иначе <c>Ctrl+C</c>, <c>Ctrl+W</c> и прочие сочетания оболочки перестали бы работать.
/// </summary>
public sealed class ShellShortcutHandler
{
    private readonly ShellViewModel _shell;
    private readonly IUserPrompt _prompt;

    /// <inheritdoc cref="ShellShortcutHandler" />
    public ShellShortcutHandler(ShellViewModel shell, IUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(prompt);

        _shell = shell;
        _prompt = prompt;
    }

    /// <summary>
    /// Обрабатывает нажатие. Сравнение идёт по физической клавише: на русской раскладке
    /// сравнение по символу не работает.
    /// </summary>
    /// <returns><c>true</c>, если сочетание адресовано окну и дальше его пускать не нужно.</returns>
    public bool Handle(Key key, ModifierKeys modifiers)
    {
        if (!ShellShortcutMap.TryMap(key, modifiers, out var shortcut, out var tabNumber))
        {
            return false;
        }

        var task = _shell.ApplyShortcutAsync(shortcut, tabNumber, CancellationToken.None);
        if (task.IsCompletedSuccessfully)
        {
            return true;
        }

        // Не async void: сбой команды показывается пользователю, а не роняет процесс.
        var scheduler = SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Exception is { } exception)
                {
                    _prompt.ShowError("Ошибка", exception.GetBaseException().Message);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            scheduler);

        return true;
    }
}
