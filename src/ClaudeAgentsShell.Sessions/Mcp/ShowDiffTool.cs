using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Инструмент <c>show_diff</c> (issue #5): разбирает аргументы агента и передаёт их приложению
/// через <see cref="IShowDiffHandler"/>. Сам ничего не строит и ответа человека не ждёт.
/// </summary>
public sealed class ShowDiffTool : IMcpTool
{
    /// <summary>Текст ошибки, когда токен не соответствует ни одной вкладке.</summary>
    internal const string UnknownSessionText =
        "No Agents Shell tab matches this session: it was started outside the Agents Shell app, "
        + "or its tab has been closed. Nothing was shown to the user.";

    /// <summary>Текст ошибки, когда обработчик сломался изнутри.</summary>
    internal const string InternalErrorText = "Agents Shell could not show the diff because of an internal error.";

    private const string ToolDescription =
        "Show the user a diff of your changes in the Agents Shell app's diff panel, right next to this terminal. "
        + "Call it when you want the user to review what you changed: after finishing a piece of work, "
        + "or when the user asks to see the diff. By default it shows everything the current branch changed "
        + "against its base branch: commits plus uncommitted and untracked files. Use `files` to point the user "
        + "at specific files and `note` to say what they are looking at. Returns immediately; "
        + "it does not wait for the user to read the diff.";

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "base": {
              "type": "string",
              "description": "Base ref to compare against: branch, tag or commit. Default: the merge-base with origin/HEAD, then main, then master."
            },
            "path": {
              "type": "string",
              "description": "Repository directory. Default: the current working directory of this session."
            },
            "files": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Repository-relative paths to show and expand. Other changed files are left out. Default: all changed files."
            },
            "note": {
              "type": "string",
              "description": "Short title shown to the user above the file list, e.g. what the change does."
            }
          },
          "additionalProperties": false
        }
        """;

    private static readonly JsonElement Schema = ParseSchema();

    private readonly IShowDiffHandler _handler;

    /// <inheritdoc cref="ShowDiffTool" />
    /// <param name="handler">Обработчик приложения: знает вкладки и панель diff.</param>
    public ShowDiffTool(IShowDiffHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc />
    public string Name => McpProtocol.ShowDiffToolName;

    /// <inheritdoc />
    public string Description => ToolDescription;

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

        ShowDiffOutcome outcome;
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
            // Сбой приложения не должен выглядеть для агента как упавший сервер: ему достаточно
            // знать, что diff не показан.
            return McpToolResult.Error(InternalErrorText);
        }

        return outcome switch
        {
            ShowDiffOutcome.Shown shown => McpToolResult.Success(shown.Summary),
            ShowDiffOutcome.Failed failed => McpToolResult.Error(failed.Message),
            _ => McpToolResult.Error(UnknownSessionText),
        };
    }

    /// <summary>
    /// Аргументы агента → запрос. Пустые строки считаются отсутствующими, неизвестные поля
    /// пропускаются; неверный тип — ошибка инструмента с понятным агенту текстом.
    /// </summary>
    private static bool TryParse(JsonElement? arguments, out ShowDiffRequest request, out string problem)
    {
        request = new ShowDiffRequest(null, null, [], null);
        problem = string.Empty;

        if (arguments is not { } args || args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            problem = "Arguments must be a JSON object.";
            return false;
        }

        if (!TryString(args, "base", out var baseRef, ref problem)
            || !TryString(args, "path", out var directory, ref problem)
            || !TryString(args, "note", out var note, ref problem)
            || !TryFiles(args, out var files, ref problem))
        {
            return false;
        }

        request = new ShowDiffRequest(baseRef, directory, files, note);
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

        var text = element.GetString();
        value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return true;
    }

    private static bool TryFiles(JsonElement args, out IReadOnlyList<string> files, ref string problem)
    {
        files = [];
        if (!args.TryGetProperty("files", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            problem = "Argument 'files' must be an array of strings.";
            return false;
        }

        var list = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                problem = "Argument 'files' must be an array of strings.";
                return false;
            }

            var path = item.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(path.Trim());
            }
        }

        files = list;
        return true;
    }

    private static JsonElement ParseSchema()
    {
        using var document = JsonDocument.Parse(SchemaJson);
        return document.RootElement.Clone();
    }
}
