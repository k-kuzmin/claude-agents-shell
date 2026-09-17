namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Разбирает JSON, пришедший со страницы. Битое или незнакомое сообщение — <c>false</c>,
/// а не исключение: страница не должна уметь ронять приложение.
/// </summary>
public interface IBridgeMessageParser
{
    /// <summary>Пытается разобрать сообщение.</summary>
    bool TryParse(string json, out InboundBridgeMessage? message);
}
