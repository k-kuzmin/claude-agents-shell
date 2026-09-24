using ClaudeAgentsShell.Domain;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Разметка уведомления «ждёт ввода» и разбор его аргумента активации. Чистый код без
/// WinRT: разметка собирается и проверяется без показа, аргумент разбирается без Windows.
/// </summary>
internal static class AwaitingToastContent
{
    /// <summary>Ключ аргумента активации с идентификатором вкладки.</summary>
    internal const string TabKey = "tab";

    /// <summary>Текст под заголовком.</summary>
    internal const string Body = "Ждёт ввода";

    /// <summary>Подпись кнопки.</summary>
    internal const string OpenButton = "Открыть";

    /// <summary>
    /// Ключ командной строки, с которым Toolkit 7.1.3 прописывает exe в
    /// <c>HKCU\Software\Classes\CLSID\{…}\LocalServer32</c>: так COM запускает закрытое
    /// приложение нажатием на уведомление.
    /// </summary>
    internal const string ToastActivatedSwitch = "-ToastActivated";

    /// <summary>
    /// Запущен ли процесс нажатием на уведомление. Своя проверка вместо
    /// <c>ToastNotificationManagerCompat.WasCurrentProcessToastActivated()</c>: тот при первом
    /// обращении регистрирует приложение в системе, и платил бы каждый старт.
    /// </summary>
    /// <param name="commandLineArgs">Аргументы процесса; нулевой — путь к exe, но он не мешает.</param>
    internal static bool IsToastActivationLaunch(IReadOnlyList<string>? commandLineArgs) =>
        commandLineArgs is not null
        && commandLineArgs.Any(arg => string.Equals(arg, ToastActivatedSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Разметка уведомления: заголовок, текст и кнопка «Открыть». И тело, и кнопка
    /// активируют приложение (foreground) с идентификатором вкладки в аргументе.
    /// </summary>
    internal static ToastContentBuilder Build(AwaitingToast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        string tab = toast.Tab.Value;

        // Аргументы только через AddArgument — и у тела, и у кнопки. Кнопка с аргументами
        // из конструктора ToastButton(content, arguments) несовместима с AddArgument
        // построителя: Toolkit бросает при сборке.
        return new ToastContentBuilder()
            .AddArgument(TabKey, tab)
            .AddText(toast.Title)
            .AddText(Body)
            .AddButton(new ToastButton()
                .SetContent(OpenButton)
                .AddArgument(TabKey, tab));
    }

    /// <summary>
    /// Достаёт вкладку из аргумента активации. Пустой, битый или чужой аргумент — не
    /// ошибка, а <see langword="false"/>: уведомление могло остаться от другой версии.
    /// </summary>
    internal static bool TryParseTab(string? argument, out TerminalId tab)
    {
        tab = default;

        if (string.IsNullOrWhiteSpace(argument))
        {
            return false;
        }

        try
        {
            if (!ToastArguments.Parse(argument).TryGetValue(TabKey, out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            tab = new TerminalId(value);
            return true;
        }
        catch (Exception)
        {
            // Разбор чужой строки: любой сбой формата означает «не наш аргумент».
            return false;
        }
    }
}
