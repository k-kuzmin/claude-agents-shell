using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Сообщение со страницы терминалов в C#. Разбор соответствует разделу 3.2 ТЗ:
/// <c>in</c>, <c>resize</c>, <c>ready</c>; служебное <c>ack</c>; сообщения панели diff
/// <c>diff.refresh</c>, <c>diff.file.request</c>, <c>diff.closed</c> (issue #5). Неизвестные типы игнорируются.
/// </summary>
public abstract record InboundBridgeMessage(TerminalId TerminalId)
{
    /// <summary>Ввод пользователя: <c>{"type":"in","id":"t1","b64":"…"}</c>.</summary>
    /// <param name="TerminalId">Вкладка-источник.</param>
    /// <param name="Data">Сырые байты, раскодированные из base64.</param>
    public sealed record Input(TerminalId TerminalId, ReadOnlyMemory<byte> Data) : InboundBridgeMessage(TerminalId);

    /// <summary>Новый размер: <c>{"type":"resize","id":"t1","cols":120,"rows":34}</c>.</summary>
    /// <param name="TerminalId">Вкладка, которой касается размер.</param>
    /// <param name="Size">Размер в знакоместах.</param>
    public sealed record Resize(TerminalId TerminalId, TerminalSize Size) : InboundBridgeMessage(TerminalId);

    /// <summary>Терминал создан и готов: <c>{"type":"ready","id":"t1"}</c>.</summary>
    /// <param name="TerminalId">Готовая вкладка.</param>
    public sealed record Ready(TerminalId TerminalId) : InboundBridgeMessage(TerminalId);

    /// <summary>
    /// Подтверждение записи пачки вывода: <c>{"type":"ack","id":"t1","bytes":1234}</c>.
    /// <para>
    /// Единственное служебное расширение протокола раздела 3.2 ТЗ. Его требует раздел 3.3:
    /// «<c>term.write(bytes, callback)</c> — счётчик незавершённых записей; если он превышает
    /// порог, чтение из PTY приостанавливается до вызова callback». Callback живёт на странице,
    /// поэтому без обратного сообщения счётчик на стороне C# посчитать нечем.
    /// Страница шлёт <c>ack</c> из колбэка <c>term.write</c>, по одному на каждое <c>out</c>.
    /// </para>
    /// </summary>
    /// <param name="TerminalId">Вкладка, подтвердившая запись.</param>
    /// <param name="Sequence">
    /// Номер пачки из соответствующего <c>out</c>. Подтверждение накрывает и все пачки
    /// с меньшими номерами: страница пишет их по порядку, поэтому потеря одной квитанции
    /// не сдвигает соответствие.
    /// </param>
    /// <param name="Bytes">Сколько байтов записано — для диагностики и сверки.</param>
    public sealed record Ack(TerminalId TerminalId, long Sequence, int Bytes) : InboundBridgeMessage(TerminalId);

    /// <summary>
    /// Панель просит построить diff заново:
    /// <c>{"type":"diff.refresh","id":"t1","dir":null,"base":null,"ws":false}</c>.
    /// </summary>
    /// <param name="TerminalId">Вкладка, в панели которой нажали.</param>
    /// <param name="Directory">Другое рабочее дерево; <c>null</c> — прежнее.</param>
    /// <param name="BaseRef">Другая база; <c>null</c> — прежняя.</param>
    /// <param name="IgnoreWhitespace">Сравнение без учёта пробелов (<c>-w</c>).</param>
    public sealed record DiffRefresh(TerminalId TerminalId, string? Directory, string? BaseRef, bool IgnoreWhitespace)
        : InboundBridgeMessage(TerminalId);

    /// <summary>
    /// Панель раскрыла файл: <c>{"type":"diff.file.request","id":"t1","path":"src/a.cs","ctx":"hunks"}</c>.
    /// </summary>
    /// <param name="TerminalId">Вкладка.</param>
    /// <param name="Path">Путь из оглавления.</param>
    /// <param name="Context"><c>hunks</c> — только фрагменты, <c>full</c> — весь файл.</param>
    public sealed record DiffFileRequest(TerminalId TerminalId, string Path, DiffContext Context)
        : InboundBridgeMessage(TerminalId);

    /// <summary>Пользователь закрыл панель: <c>{"type":"diff.closed","id":"t1"}</c>.</summary>
    /// <param name="TerminalId">Вкладка.</param>
    public sealed record DiffClosed(TerminalId TerminalId) : InboundBridgeMessage(TerminalId);
}
