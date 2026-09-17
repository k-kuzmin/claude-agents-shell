using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Сообщение со страницы терминалов в C#. Разбор соответствует разделу 3.2 ТЗ:
/// <c>in</c>, <c>resize</c>, <c>ready</c>. Неизвестные типы игнорируются.
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
}
