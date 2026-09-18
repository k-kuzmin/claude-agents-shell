using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Пользовательский ввод со страницы: сырые байты, готовые для stdin.</summary>
public sealed class TerminalInputEventArgs(TerminalId terminalId, ReadOnlyMemory<byte> data) : EventArgs
{
    /// <summary>Вкладка, из которой пришёл ввод.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Сырые байты ввода.</summary>
    public ReadOnlyMemory<byte> Data { get; } = data;
}

/// <summary>Страница пересчитала размер терминала после <c>fit()</c>.</summary>
public sealed class TerminalResizeEventArgs(TerminalId terminalId, TerminalSize size) : EventArgs
{
    /// <summary>Вкладка, которой касается размер.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Новый размер в знакоместах.</summary>
    public TerminalSize Size { get; } = size;
}

/// <summary>Экземпляр xterm.js на странице создан и готов принимать вывод.</summary>
public sealed class TerminalReadyEventArgs(TerminalId terminalId) : EventArgs
{
    /// <summary>Вкладка, которая готова.</summary>
    public TerminalId TerminalId { get; } = terminalId;
}

/// <summary>
/// Мост между C# и страницей терминалов. Ровно один на окно: на странице живут N экземпляров
/// xterm.js, переключение вкладки — смена видимости контейнера, а не новый контрол.
/// Реализация владеет WebView2; прикладной код о нём не знает.
/// </summary>
public interface ITerminalBridge : IAsyncDisposable
{
    /// <summary>Пришёл ввод пользователя.</summary>
    event EventHandler<TerminalInputEventArgs>? InputReceived;

    /// <summary>Страница просит сменить размер псевдоконсоли.</summary>
    event EventHandler<TerminalResizeEventArgs>? ResizeRequested;

    /// <summary>Терминал на странице создан и готов.</summary>
    event EventHandler<TerminalReadyEventArgs>? TerminalReady;

    /// <summary>Поднимает движок страницы и ждёт его готовности.</summary>
    /// <exception cref="TerminalBridgeUnavailableException">Среда для страницы недоступна.</exception>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Просит страницу создать терминал с этим идентификатором.</summary>
    ValueTask CreateTerminalAsync(TerminalId terminalId, string title, CancellationToken cancellationToken);

    /// <summary>Делает терминал видимым; остальные скрываются.</summary>
    ValueTask ShowTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>Уничтожает терминал на странице и освобождает его ресурсы.</summary>
    ValueTask CloseTerminalAsync(TerminalId terminalId, CancellationToken cancellationToken);

    /// <summary>
    /// Отправляет накопленную пачку сырых байтов вывода. Байты уходят в base64 без
    /// декодирования в строку; склейка в пачки — обязанность вызывающего.
    /// <para>
    /// <b>Задача завершается не по факту отправки, а по подтверждению страницы</b> —
    /// колбэку <c>term.write</c>. На этом построен счётчик незавершённых записей из
    /// раздела 3.3 ТЗ: вызывающий держит не больше
    /// <c>TerminalOptions.MaxPendingWrites</c> незавершённых вызовов и на это время
    /// приостанавливает чтение из PTY. Реализация, завершающая задачу сразу, молча
    /// отключает backpressure.
    /// </para>
    /// <para>
    /// <paramref name="payload"/> вычитывается до первого <c>await</c>: вызывающий вправе
    /// вернуть буфер в пул, как только метод отдал управление.
    /// </para>
    /// </summary>
    ValueTask WriteOutputAsync(TerminalId terminalId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>Сообщает странице, что процесс вкладки завершился.</summary>
    ValueTask NotifyExitedAsync(TerminalId terminalId, int exitCode, CancellationToken cancellationToken);
}

/// <summary>
/// Среда для страницы терминалов недоступна: не нашлись локальные ассеты, не загрузился
/// документ, не поднялся движок. Случай «движок вообще не установлен» вынесен в наследника
/// <see cref="TerminalRuntimeMissingException"/>.
/// </summary>
public class TerminalBridgeUnavailableException : Exception
{
    /// <inheritdoc cref="TerminalBridgeUnavailableException" />
    public TerminalBridgeUnavailableException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="TerminalBridgeUnavailableException" />
    public TerminalBridgeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Движка страницы нет в системе: не установлен рантайм браузера (раздел 8 ТЗ).
/// </summary>
/// <remarks>
/// Отдельный тип, а не признак внутри <see cref="TerminalBridgeUnavailableException"/>:
/// только этот сбой пользователь чинит сам, установив рантайм, и только ему нужен разговор
/// со ссылкой на установщик. Прочие сбои поднятия страницы — отсутствие каталога <c>web</c>,
/// неудачная навигация — это дефекты сборки или окружения, и ссылка там не поможет.
/// Наследование оставляет прикладному коду право ловить базовый тип там, где разница не важна.
/// </remarks>
public sealed class TerminalRuntimeMissingException : TerminalBridgeUnavailableException
{
    /// <inheritdoc cref="TerminalRuntimeMissingException" />
    public TerminalRuntimeMissingException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="TerminalRuntimeMissingException" />
    public TerminalRuntimeMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
