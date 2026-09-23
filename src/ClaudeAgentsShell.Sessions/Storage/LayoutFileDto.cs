using System.Text.Json.Serialization;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Слепок <c>layout.json</c> ровно в том виде, в каком он лежит на диске (issue #4).
/// Все поля допускают <c>null</c>: битая запись пропускается, а не роняет разбор.
/// </summary>
internal sealed class LayoutFileDto
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("activeProjectId")]
    public string? ActiveProjectId { get; set; }

    [JsonPropertyName("projects")]
    public List<ProjectLayoutDto?>? Projects { get; set; }
}

/// <summary>Вкладки одного проекта на диске.</summary>
internal sealed class ProjectLayoutDto
{
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("activeTabIndex")]
    public int? ActiveTabIndex { get; set; }

    [JsonPropertyName("tabs")]
    public List<TabLayoutDto?>? Tabs { get; set; }
}

/// <summary>Одна вкладка на диске.</summary>
internal sealed class TabLayoutDto
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
