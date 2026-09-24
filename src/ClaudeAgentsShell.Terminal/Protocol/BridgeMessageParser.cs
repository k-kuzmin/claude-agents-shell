using System.Text.Json;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Разбор входящих сообщений страницы. Любая неожиданность — <c>false</c>: битый JSON,
/// незнакомый <c>type</c>, отсутствующие или неверные по типу поля, кривой base64.
/// Страница не должна уметь ронять приложение.
/// </summary>
public sealed class BridgeMessageParser : IBridgeMessageParser
{
    /// <inheritdoc />
    public bool TryParse(string json, out InboundBridgeMessage? message)
    {
        message = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "type", out string type) ||
                !TryGetString(root, "id", out string id) ||
                string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var terminalId = new TerminalId(id);

            switch (type)
            {
                case "in":
                    return TryParseInput(root, terminalId, out message);

                case "resize":
                    return TryParseResize(root, terminalId, out message);

                case "ready":
                    message = new InboundBridgeMessage.Ready(terminalId);
                    return true;

                case "ack":
                    if (!TryGetInt64(root, "seq", out long sequence) || sequence < 0)
                    {
                        return false;
                    }

                    message = new InboundBridgeMessage.Ack(
                        terminalId,
                        sequence,
                        TryGetInt32(root, "bytes", out int bytes) ? bytes : 0);
                    return true;

                case "diff.refresh":
                    return TryParseDiffRefresh(root, terminalId, out message);

                case "diff.file.request":
                    return TryParseDiffFileRequest(root, terminalId, out message);

                case "diff.closed":
                    message = new InboundBridgeMessage.DiffClosed(terminalId);
                    return true;

                case "paste.request":
                    message = new InboundBridgeMessage.PasteRequest(terminalId);
                    return true;

                case "drop":
                    // Пути в JSON не приходят: их подставляет хост из AdditionalObjects.
                    message = new InboundBridgeMessage.FilesDropped(terminalId, []);
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Пустой или пробельный идентификатор вкладки.
            return false;
        }
    }

    private static bool TryParseInput(JsonElement root, TerminalId terminalId, out InboundBridgeMessage? message)
    {
        message = null;

        if (!TryGetString(root, "b64", out string base64))
        {
            return false;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        message = new InboundBridgeMessage.Input(terminalId, data);
        return true;
    }

    /// <summary>
    /// Размер разбирается как есть, включая нулевой: отбрасывать невалидные значения —
    /// обязанность вызывающего (см. <c>IPtySession.Resize</c>).
    /// </summary>
    private static bool TryParseResize(JsonElement root, TerminalId terminalId, out InboundBridgeMessage? message)
    {
        message = null;

        if (!TryGetInt32(root, "cols", out int cols) || !TryGetInt32(root, "rows", out int rows))
        {
            return false;
        }

        message = new InboundBridgeMessage.Resize(terminalId, new TerminalSize(cols, rows));
        return true;
    }

    /// <summary>
    /// <c>dir</c> и <c>base</c> необязательны: отсутствие, <c>null</c> и пустая строка значат
    /// «прежнее». Любой другой тип — битое сообщение. <c>ws</c> обязателен.
    /// </summary>
    private static bool TryParseDiffRefresh(JsonElement root, TerminalId terminalId, out InboundBridgeMessage? message)
    {
        message = null;

        if (!TryGetOptionalString(root, "dir", out string? directory) ||
            !TryGetOptionalString(root, "base", out string? baseRef) ||
            !root.TryGetProperty("ws", out var ws) ||
            ws.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        message = new InboundBridgeMessage.DiffRefresh(terminalId, directory, baseRef, ws.GetBoolean());
        return true;
    }

    private static bool TryParseDiffFileRequest(JsonElement root, TerminalId terminalId, out InboundBridgeMessage? message)
    {
        message = null;

        if (!TryGetString(root, "path", out string path) ||
            string.IsNullOrEmpty(path) ||
            !TryGetString(root, "ctx", out string ctx))
        {
            return false;
        }

        DiffContext context;
        switch (ctx)
        {
            case "hunks":
                context = DiffContext.Hunks;
                break;
            case "full":
                context = DiffContext.FullFile;
                break;
            default:
                return false;
        }

        message = new InboundBridgeMessage.DiffFileRequest(terminalId, path, context);
        return true;
    }

    private static bool TryGetOptionalString(JsonElement root, string name, out string? value)
    {
        value = null;

        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = element.GetString() ?? string.Empty;
        value = text.Length == 0 ? null : text;
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;

        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value);
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }
}
