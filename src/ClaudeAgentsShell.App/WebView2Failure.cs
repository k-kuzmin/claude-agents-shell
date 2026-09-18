using ClaudeAgentsShell.Application.Ports;
using Microsoft.Web.WebView2.Core;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Разбор сбоя поднятия WebView2: отсутствие рантайма отделяется от всех прочих причин.
/// Живёт рядом с мостом, потому что знает про типы WebView2, и отдельно от него, потому что
/// сам мост в тестах не поднять — а решение «рантайма нет» проверить надо.
/// </summary>
internal static class WebView2Failure
{
    /// <summary>Ссылка на страницу загрузки Evergreen WebView2 Runtime.</summary>
    internal const string InstallerUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private const string RuntimeMissingMessage =
        "В системе не установлен Microsoft Edge WebView2 Runtime. "
        + "Приложение рисует терминалы на его движке, поэтому без рантайма работать не может.";

    // Про установку здесь ничего не утверждается: признак отсутствия рантайма мог и не
    // дойти до нас — например, рантайм есть, но повреждён, и движок отказал безымянным
    // COM-сбоем. Обещать пользователю, что «движок установлен», в этой ветке нельзя.
    private const string GenericMessage = "Не удалось запустить движок WebView2.";

    /// <summary>
    /// Превращает сбой инициализации в исключение контракта:
    /// <see cref="TerminalRuntimeMissingException"/>, если рантайма нет, иначе —
    /// <see cref="TerminalBridgeUnavailableException"/>.
    /// </summary>
    internal static TerminalBridgeUnavailableException Describe(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return IsRuntimeMissing(failure)
            ? new TerminalRuntimeMissingException(RuntimeMissingMessage, failure)
            : new TerminalBridgeUnavailableException(GenericMessage, failure);
    }

    /// <summary>
    /// Ищет признак отсутствующего рантайма во всей цепочке причин, а не только в верхнем
    /// исключении. WPF-контрол поднимает движок через задачу и отдаёт отказ обёрнутым —
    /// то <see cref="AggregateException"/>, то <see cref="InvalidOperationException"/> с
    /// исходной причиной внутри. Проверка одного верхнего типа поэтому ненадёжна, а
    /// промахнуться незаметно: воспроизвести этот путь на машине с установленным рантаймом
    /// нельзя.
    /// </summary>
    internal static bool IsRuntimeMissing(Exception? failure)
    {
        // Глубина ограничена: цепочка причин в теории может оказаться закольцованной,
        // и тогда обход висел бы вечно.
        const int MaxDepth = 16;

        for (int depth = 0; failure is not null && depth < MaxDepth; depth++)
        {
            if (failure is WebView2RuntimeNotFoundException)
            {
                return true;
            }

            if (failure is AggregateException aggregate)
            {
                // InnerException у агрегата — только первое из исключений; остальные
                // потерялись бы, а нужное может быть любым из них.
                return aggregate.InnerExceptions.Any(static inner => IsRuntimeMissing(inner));
            }

            failure = failure.InnerException;
        }

        return false;
    }
}
