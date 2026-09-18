using System.Text.Json;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Разбор тела запроса от хука. Формат полезной нагрузки Claude Code считается нестабильным:
/// всё, что не разобралось, — не событие, а не исключение.
/// </summary>
internal static class HookPayload
{
    private const string EventNameField = "hook_event_name";
    private const string SessionIdField = "session_id";
    private const string WorkingDirectoryField = "cwd";

    /// <summary>
    /// Событие либо <c>null</c>, если тело не разобралось или хук незнакомый:
    /// незнакомые хуки игнорируются молча.
    /// </summary>
    public static HookEvent? TryParse(string? body, string? token, DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var kind = ParseKind(ReadString(root, EventNameField));
            if (kind == HookKind.Unknown)
            {
                return null;
            }

            return new HookEvent(
                kind,
                ReadString(root, SessionIdField),
                ReadString(root, WorkingDirectoryField),
                string.IsNullOrWhiteSpace(token) ? ReadString(root, HookProtocol.TokenPayloadField) : token,
                receivedUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Имя хука из полезной нагрузки в <see cref="HookKind"/>. Незнакомое имя —
    /// <see cref="HookKind.Unknown"/>, то есть не событие.
    /// </summary>
    /// <remarks>
    /// Поля, которые хуки приносят сверх <c>session_id</c> и <c>cwd</c>, не разбираются:
    /// <c>agent_id</c> и <c>agent_type</c> у сабагентов, <c>user_input</c> у промпта.
    /// Состояние вкладки от них не зависит — счётчик сабагентов приложение ведёт само,
    /// а текст промпта ему не нужен вовсе, — и расширять ради них <see cref="HookEvent"/>,
    /// зафиксированный контрактом этапа, не за чем.
    /// </remarks>
    private static HookKind ParseKind(string? name) => name switch
    {
        "SessionStart" => HookKind.SessionStart,
        "Stop" => HookKind.Stop,
        "SessionEnd" => HookKind.SessionEnd,
        "UserPromptSubmit" => HookKind.UserPromptSubmit,
        "SubagentStart" => HookKind.SubagentStart,
        "SubagentStop" => HookKind.SubagentStop,
        _ => HookKind.Unknown,
    };

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;
}
