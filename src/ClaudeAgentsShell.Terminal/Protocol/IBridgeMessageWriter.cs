using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Собирает JSON для отправки на страницу. Формат — раздел 3.2 ТЗ.
/// Горячий метод здесь один — <see cref="Out"/>; он вызывается на каждую пачку вывода,
/// поэтому реализация не должна аллоцировать промежуточные строки и объекты сверх самой base64.
/// </summary>
public interface IBridgeMessageWriter
{
    /// <summary>
    /// <c>{"type":"out","id":"t1","seq":12,"b64":"…"}</c> — пачка сырых байтов PTY.
    /// Байты не декодируются в строку ни здесь, ни выше по стеку.
    /// </summary>
    /// <param name="terminalId">Вкладка-получатель.</param>
    /// <param name="sequence">
    /// Неубывающий номер пачки в пределах вкладки. Страница возвращает его в <c>ack</c>,
    /// и по нему запись сопоставляется с ожиданием: считать квитанции по порядку нельзя —
    /// одна потерянная сдвинула бы соответствие навсегда.
    /// </param>
    /// <param name="payload">Сырые байты пачки.</param>
    string Out(TerminalId terminalId, long sequence, ReadOnlySpan<byte> payload);

    /// <summary><c>{"type":"create","id":"t1","title":"…"}</c></summary>
    string Create(TerminalId terminalId, string title);

    /// <summary><c>{"type":"show","id":"t1"}</c></summary>
    string Show(TerminalId terminalId);

    /// <summary><c>{"type":"close","id":"t1"}</c></summary>
    string Close(TerminalId terminalId);

    /// <summary><c>{"type":"exited","id":"t1","code":0}</c></summary>
    string Exited(TerminalId terminalId, int exitCode);
}
