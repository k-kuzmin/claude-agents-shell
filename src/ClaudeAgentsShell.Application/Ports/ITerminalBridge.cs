using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Ввод со страницы: сырые байты, готовые для stdin.</summary>
/// <param name="terminalId">Вкладка, из которой пришёл ввод.</param>
/// <param name="data">Сырые байты ввода.</param>
/// <param name="fromUser">Байты набраны или вставлены человеком, а не сгенерированы терминалом.</param>
public sealed class TerminalInputEventArgs(TerminalId terminalId, ReadOnlyMemory<byte> data, bool fromUser)
    : EventArgs
{
    /// <summary>Вкладка, из которой пришёл ввод.</summary>
    public TerminalId TerminalId { get; } = terminalId;

    /// <summary>Сырые байты ввода.</summary>
    public ReadOnlyMemory<byte> Data { get; } = data;

    /// <summary>
    /// Байты пришли от человека — нажатие клавиши или вставка, — а не от самого терминала.
    /// </summary>
    /// <remarks>
    /// Различать обязательно. В stdin уходит и то и другое, но «пользователь работает»
    /// означает только первое. Страница отдаёт одним каналом с нажатиями ещё и ответы
    /// терминала на запросы программы: отчёт о получении и потере фокуса (<c>ESC [ I</c>,
    /// <c>ESC [ O</c>), ответ на запрос Device Attributes (<c>ESC [ ?1;2c</c>), ответ
    /// на запрос цвета и отчёты о размере окна и знакоместа. По содержимому байтов их
    /// не отфильтровать надёжно, поэтому признак ставит страница, где источник события известен.
    /// <para>
    /// Замечено живьём: показ вкладки зовёт <c>term.focus()</c>, терминал отвечает
    /// <c>ESC [ I</c>, и переключение вкладок выглядело как ввод — точка состояния сбрасывалась
    /// в «работает», а счётчик «N ждёт ввода» обнулял сам себя по клику.
    /// </para>
    /// </remarks>
    public bool FromUser { get; } = fromUser;
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

/// <summary>Среда для страницы терминалов недоступна — например, не установлен WebView2 Runtime.</summary>
public sealed class TerminalBridgeUnavailableException : Exception
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
