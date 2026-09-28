using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Инструмент <c>show_file</c>: разбирает аргументы агента и передаёт их приложению через
/// <see cref="IShowFileHandler"/>. Файлы сам не читает и ответа человека не ждёт.
/// </summary>
public sealed class ShowFileTool : IMcpTool
{
    /// <summary>Сколько файлов можно показать одним вызовом.</summary>
    public const int MaxFiles = 20;

    /// <summary>Текст ошибки, когда токен не соответствует ни одной вкладке.</summary>
    internal const string UnknownSessionText =
        "No Agents Shell tab matches this session: it was started outside the Agents Shell app, "
        + "or its tab has been closed. Nothing was shown to the user.";

    /// <summary>Текст ошибки, когда обработчик сломался изнутри.</summary>
    internal const string InternalErrorText = "Agents Shell could not show the files because of an internal error.";

    private const string FilesShapeText =
        "Argument 'files' must be a non-empty array; each item is a path string or an object "
        + "{ \"path\": string, \"start_line\"?: integer, \"end_line\"?: integer }.";

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "files": {
              "type": "array",
              "minItems": 1,
              "maxItems": 20,
              "items": {
                "anyOf": [
                  { "type": "string", "description": "File path; the whole file is shown from the top." },
                  {
                    "type": "object",
                    "properties": {
                      "path": { "type": "string", "description": "File path." },
                      "start_line": { "type": "integer", "minimum": 1, "description": "First line to scroll to and highlight, 1-based." },
                      "end_line": { "type": "integer", "minimum": 1, "description": "Last highlighted line, inclusive. Requires start_line. Default: start_line." }
                    },
                    "required": ["path"],
                    "additionalProperties": false
                  }
                ]
              },
              "description": "Files to show, in this order. Paths are relative to `path` or absolute, and must be inside the repository."
            },
            "path": {
              "type": "string",
              "description": "Directory the paths are relative to. Default: the current working directory of this session."
            },
            "note": {
              "type": "string",
              "description": "Short title shown to the user above the files, e.g. what to look at."
            }
          },
          "required": ["files"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonElement Schema = ParseSchema();

    private readonly IShowFileHandler _handler;

    /// <inheritdoc cref="ShowFileTool" />
    /// <param name="handler">Обработчик приложения: знает вкладки и панель.</param>
    public ShowFileTool(IShowFileHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc />
    public string Name => McpProtocol.ShowFileToolName;

    /// <inheritdoc />
    public string Description => McpServerTexts.ShowFileDescription;

    /// <inheritdoc />
    public JsonElement InputSchema => Schema;

    /// <inheritdoc />
    public async Task<McpToolResult> CallAsync(
        string? correlationToken,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParse(arguments, out var request, out var problem))
        {
            return McpToolResult.Error(problem);
        }

        ShowFileOutcome outcome;
        try
        {
            outcome = await _handler.HandleAsync(correlationToken, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Сбой приложения не должен выглядеть для агента как упавший сервер.
            return McpToolResult.Error(InternalErrorText);
        }

        return outcome switch
        {
            ShowFileOutcome.Shown shown => McpToolResult.Success(shown.Summary),
            ShowFileOutcome.Failed failed => McpToolResult.Error(failed.Message),
            _ => McpToolResult.Error(UnknownSessionText),
        };
    }

    /// <summary>
    /// Аргументы агента → запрос. Пустые строки считаются отсутствующими, неизвестные поля
    /// верхнего уровня пропускаются; неверная форма — ошибка инструмента с понятным агенту текстом.
    /// </summary>
    private static bool TryParse(JsonElement? arguments, out ShowFileRequest request, out string problem)
    {
        request = new ShowFileRequest(null, [], null);
        problem = string.Empty;

        if (arguments is not { } args || args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            problem = "Argument 'files' is required.";
            return false;
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            problem = "Arguments must be a JSON object.";
            return false;
        }

        if (!TryString(args, "path", out var directory, ref problem)
            || !TryString(args, "note", out var note, ref problem)
            || !TryFiles(args, out var files, ref problem))
        {
            return false;
        }

        request = new ShowFileRequest(directory, files, note);
        return true;
    }

    private static bool TryString(JsonElement args, string name, out string? value, ref string problem)
    {
        value = null;
        if (!args.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            problem = $"Argument '{name}' must be a string.";
            return false;
        }

        value = Trimmed(element.GetString());
        return true;
    }

    private static bool TryFiles(JsonElement args, out IReadOnlyList<ShowFileItem> files, ref string problem)
    {
        files = [];
        if (!args.TryGetProperty("files", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            problem = "Argument 'files' is required.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            problem = FilesShapeText;
            return false;
        }

        var list = new List<ShowFileItem>(Math.Min(element.GetArrayLength(), MaxFiles));
        foreach (var entry in element.EnumerateArray())
        {
            ShowFileItem? item;
            switch (entry.ValueKind)
            {
                case JsonValueKind.String:
                    var text = Trimmed(entry.GetString());
                    item = text is null ? null : new ShowFileItem(text, null);
                    break;
                case JsonValueKind.Object:
                    if (!TryItem(entry, out item, ref problem))
                    {
                        return false;
                    }

                    break;
                default:
                    problem = FilesShapeText;
                    return false;
            }

            if (item is not null)
            {
                list.Add(item);
            }
        }

        if (list.Count == 0)
        {
            problem = FilesShapeText;
            return false;
        }

        if (list.Count > MaxFiles)
        {
            problem = $"Too many files: {list.Count}. Show at most {MaxFiles} files per call.";
            return false;
        }

        files = list;
        return true;
    }

    private static bool TryItem(JsonElement entry, out ShowFileItem? item, ref string problem)
    {
        item = null;
        if (!entry.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
            || Trimmed(pathElement.GetString()) is not { } path)
        {
            problem = "Each object in 'files' needs a non-empty string 'path'.";
            return false;
        }

        if (!TryLine(entry, "start_line", out var start, ref problem)
            || !TryLine(entry, "end_line", out var end, ref problem))
        {
            return false;
        }

        if (start is null)
        {
            if (end is not null)
            {
                problem = $"File '{path}': 'end_line' requires 'start_line'.";
                return false;
            }

            item = new ShowFileItem(path, null);
            return true;
        }

        var to = end ?? start.Value;
        if (to < start.Value)
        {
            problem = $"File '{path}': 'end_line' must not be less than 'start_line'.";
            return false;
        }

        item = new ShowFileItem(path, new LineRange(start.Value, to));
        return true;
    }

    private static bool TryLine(JsonElement entry, string name, out int? line, ref string problem)
    {
        line = null;
        if (!entry.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 1)
        {
            problem = $"'{name}' must be an integer, 1 or greater.";
            return false;
        }

        line = value;
        return true;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static JsonElement ParseSchema()
    {
        using var document = JsonDocument.Parse(SchemaJson);
        return document.RootElement.Clone();
    }
}
