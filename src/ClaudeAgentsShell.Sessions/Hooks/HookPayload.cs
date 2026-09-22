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
    private const string SourceField = "source";
    private const string AgentIdField = "agent_id";
    private const string BackgroundTasksField = "background_tasks";
    private const string TaskIdField = "id";
    private const string TaskTypeField = "type";

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
                receivedUtc,
                ReadString(root, SourceField),
                ReadString(root, AgentIdField),
                ReadBackgroundTasks(root));
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
    /// Поля, которые хуки приносят сверх разобранных, не читаются: <c>agent_type</c>
    /// у сабагентов, <c>prompt</c> у промпта, <c>error</c> и <c>error_details</c>
    /// у <c>StopFailure</c>, <c>session_crons</c> у конца хода. Состояние вкладки от них
    /// не зависит.
    /// </remarks>
    private static HookKind ParseKind(string? name) => name switch
    {
        "SessionStart" => HookKind.SessionStart,
        "Stop" => HookKind.Stop,
        "StopFailure" => HookKind.StopFailure,
        "SessionEnd" => HookKind.SessionEnd,
        "UserPromptSubmit" => HookKind.UserPromptSubmit,
        "SubagentStart" => HookKind.SubagentStart,
        "SubagentStop" => HookKind.SubagentStop,
        "PostToolBatch" => HookKind.PostToolBatch,
        "PermissionRequest" => HookKind.PermissionRequest,
        _ => HookKind.Unknown,
    };

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    /// <summary>
    /// Поле <c>background_tasks</c>: <c>null</c>, если поля нет или оно не разобралось,
    /// пустой список — если фоновых задач нет.
    /// </summary>
    /// <remarks>
    /// Любой мусор делает <c>null</c> всё поле, а не укорачивает список: короткий список
    /// сказал бы координатору «фоновой работы нет», и вкладка показала бы «ждёт ввода»
    /// при живых задачах. <c>null</c> же означает «неизвестно» — прежнее поведение.
    /// </remarks>
    private static IReadOnlyList<BackgroundTask>? ReadBackgroundTasks(JsonElement root)
    {
        if (!root.TryGetProperty(BackgroundTasksField, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tasks = new List<BackgroundTask>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            tasks.Add(new BackgroundTask(ReadString(item, TaskIdField), ReadString(item, TaskTypeField)));
        }

        return tasks;
    }
}
