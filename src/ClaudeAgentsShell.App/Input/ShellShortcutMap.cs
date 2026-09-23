using System.Windows.Input;

namespace ClaudeAgentsShell.App.Input;

/// <summary>
/// Разбор сочетаний клавиш, адресованных окну. Всё, что не распознано, остаётся терминалу.
/// <para>
/// ТЗ (раздел 6.3) называет <c>Ctrl+T</c> и <c>Ctrl+W</c>, но в оболочке это transpose-chars
/// и kill-word — их работа проверялась как пункт приёмки M1. Поэтому оконные команды живут
/// на <c>Ctrl+Shift</c>, а <c>Ctrl+T</c> и <c>Ctrl+W</c> уходят в оболочку нетронутыми.
/// </para>
/// <para>
/// Сравнение только по физической кнопке (<see cref="Key"/>). Сравнение по введённому символу
/// молча перестаёт работать на неанглийской раскладке.
/// </para>
/// </summary>
public static class ShellShortcutMap
{
    /// <summary>Наибольший номер вкладки, доступный по <c>Ctrl+&lt;цифра&gt;</c>.</summary>
    public const int MaxTabNumber = 9;

    /// <summary>
    /// Распознаёт сочетание. Возвращает <c>false</c>, если окну оно не адресовано —
    /// тогда обработчик обязан оставить событие необработанным.
    /// </summary>
    /// <param name="key">Физическая клавиша.</param>
    /// <param name="modifiers">Нажатые модификаторы.</param>
    /// <param name="shortcut">Распознанная команда.</param>
    /// <param name="tabNumber">Номер вкладки для <see cref="ShellShortcut.SelectTab"/>, иначе 0.</param>
    public static bool TryMap(Key key, ModifierKeys modifiers, out ShellShortcut shortcut, out int tabNumber)
    {
        shortcut = ShellShortcut.None;
        tabNumber = 0;

        // Alt и Win не участвуют ни в одном оконном сочетании: их наличие сразу отдаёт клавишу
        // терминалу, иначе Ctrl+Alt+цифра (AltGr на части раскладок) воровал бы переключение.
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != ModifierKeys.None)
        {
            return false;
        }

        if ((modifiers & ModifierKeys.Control) == ModifierKeys.None)
        {
            return false;
        }

        var shift = (modifiers & ModifierKeys.Shift) != ModifierKeys.None;

        switch (key)
        {
            case Key.T when shift:
                shortcut = ShellShortcut.NewSession;
                return true;

            case Key.W when shift:
                shortcut = ShellShortcut.CloseTab;
                return true;

            // Только с Shift: голый Ctrl+D — конец ввода для оболочки и claude.
            case Key.D when shift:
                shortcut = ShellShortcut.ShowDiff;
                return true;

            case Key.Tab:
                shortcut = shift ? ShellShortcut.PreviousTab : ShellShortcut.NextTab;
                return true;

            default:
                if (shift)
                {
                    return false;
                }

                var number = TabNumber(key);
                if (number == 0)
                {
                    return false;
                }

                shortcut = ShellShortcut.SelectTab;
                tabNumber = number;
                return true;
        }
    }

    // Цифровой ряд и цифровая клавиатура дают один и тот же номер: на обеих раскладках
    // пользователя это одна и та же физическая цифра.
    private static int TabNumber(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => 0,
    };
}
