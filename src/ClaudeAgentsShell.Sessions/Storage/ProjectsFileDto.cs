using System.Text.Json.Serialization;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Слепок <c>projects.json</c> ровно в том виде, в каком он лежит на диске (раздел 4.1 ТЗ).
/// Все поля допускают <c>null</c>: файл пишет не только приложение, и битую запись нужно
/// пропустить, а не уронить разбор.
/// </summary>
internal sealed class ProjectsFileDto
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("projects")]
    public List<ProjectEntryDto?>? Projects { get; set; }
}

/// <summary>Одна запись списка проектов на диске.</summary>
internal sealed class ProjectEntryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("shell")]
    public string? Shell { get; set; }

    [JsonPropertyName("preLaunch")]
    public string? PreLaunch { get; set; }

    [JsonPropertyName("extraArgs")]
    public List<string?>? ExtraArgs { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }
}
