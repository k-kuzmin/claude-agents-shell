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
