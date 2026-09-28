using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Mcp;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>Инструмент <c>show_file</c>: схема, разбор аргументов, ответы агенту.</summary>
public sealed class ShowFileToolTests
{
    private const string Token = "tab-token";

    [Fact]
    public void Схема_требует_files_и_описывает_элемент_строкой_или_объектом()
    {
        var tool = new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok")));

        Assert.Equal("show_file", tool.Name);
        var schema = tool.InputSchema;
        Assert.Equal(["files"], schema.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
        Assert.Equal(
            ["files", "note", "path"],
            schema.GetProperty("properties").EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));

        var files = schema.GetProperty("properties").GetProperty("files");
        Assert.Equal(1, files.GetProperty("minItems").GetInt32());
        Assert.Equal(ShowFileTool.MaxFiles, files.GetProperty("maxItems").GetInt32());
        var variants = files.GetProperty("items").GetProperty("anyOf").EnumerateArray().ToList();
        Assert.Equal(["string", "object"], variants.Select(static v => v.GetProperty("type").GetString()));
        Assert.Equal(
            ["end_line", "path", "start_line"],
            variants[1].GetProperty("properties").EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Строки_и_объекты_разбираются_в_запрос()
    {
        var handler = new RecordingShowFileHandler(new ShowFileOutcome.Shown("Shown 3 files."));
        var tool = new ShowFileTool(handler);

        var result = await tool.CallAsync(Token, Args("""
            {"path":" D:\\src\\r ","note":"Где ошибка","extra":1,"files":[
              "a.cs", " ",
              {"path":"b.cs","start_line":10,"end_line":20},
              {"path":"c.cs","start_line":7},
              {"path":"d.cs","start_line":null}
            ]}
            """), CancellationToken.None);

        Assert.Equal(McpToolResult.Success("Shown 3 files."), result);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(Token, call.Token);
        Assert.Equal(@"D:\src\r", call.Request.Directory);
        Assert.Equal("Где ошибка", call.Request.Note);
        Assert.Equal(
            [
                new ShowFileItem("a.cs", null),
                new ShowFileItem("b.cs", new LineRange(10, 20)),
                new ShowFileItem("c.cs", new LineRange(7, 7)),
                new ShowFileItem("d.cs", null),
            ],
            call.Request.Files);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"files":"a.cs"}""")]
    [InlineData("""{"files":[]}""")]
    [InlineData("""{"files":["  "]}""")]
    [InlineData("""{"files":[1]}""")]
    [InlineData("""{"files":[{"start_line":1}]}""")]
    [InlineData("""{"files":[{"path":" "}]}""")]
    [InlineData("""{"files":[{"path":"a.cs","end_line":3}]}""")]
    [InlineData("""{"files":[{"path":"a.cs","start_line":5,"end_line":3}]}""")]
    [InlineData("""{"files":[{"path":"a.cs","start_line":0}]}""")]
    [InlineData("""{"files":[{"path":"a.cs","start_line":1.5}]}""")]
    [InlineData("""{"files":[{"path":"a.cs","start_line":"3"}]}""")]
    [InlineData("""{"files":["a.cs"],"path":5}""")]
    [InlineData("""{"files":["a.cs"],"note":true}""")]
    [InlineData("""{"files":["a.cs"],"path":"\\\\host\\share"}""")]
    [InlineData("""{"files":["a.cs"],"path":"//host/share"}""")]
    [InlineData("""{"files":["\\\\host\\share\\a.cs"]}""")]
    [InlineData("""{"files":[{"path":"\\\\?\\C:\\a.cs","start_line":1}]}""")]
    [InlineData("""{"files":["\\\\.\\pipe\\x"]}""")]
    [InlineData("""{"files":["\\??\\C:\\a.cs"]}""")]
    [InlineData("""{"files":["a.cs"],"path":"\\??\\UNC\\host\\share"}""")]
    [InlineData("""{"files":["\\foo"]}""")]
    [InlineData("""{"files":["/foo"]}""")]
    [InlineData("""{"files":["C:foo"]}""")]
    [InlineData("""{"files":[{"path":"a:b"}]}""")]
    public async Task Неверные_аргументы_возвращают_ошибку_и_не_доходят_до_приложения(string arguments)
    {
        var handler = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));
        var tool = new ShowFileTool(handler);

        var result = await tool.CallAsync(Token, Args(arguments), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("""{"path":"\\\\host\\share"}""")]
    [InlineData("""{"path":" //host/share/repo"}""")]
    [InlineData("""{"files":["\\\\?\\C:\\x"]}""")]
    [InlineData("""{"path":"\\\\.\\C:\\repo"}""")]
    [InlineData("""{"path":"\\??\\C:\\repo"}""")]
    [InlineData("""{"path":"\\??\\UNC\\host\\share"}""")]
    [InlineData("""{"path":"\\repo"}""")]
    [InlineData("""{"path":"C:repo"}""")]
    [InlineData("""{"files":["a:b"]}""")]
    public async Task Show_diff_отклоняет_сетевые_и_device_пути_до_приложения(string arguments)
    {
        var handler = new RecordingShowDiffHandler(new ShowDiffOutcome.Shown("ok"));
        var tool = new ShowDiffTool(handler);

        var result = await tool.CallAsync(Token, Args(arguments), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("UNC", result.Text, StringComparison.Ordinal);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Локальный_абсолютный_path_разрешён()
    {
        var handler = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));

        var result = await new ShowFileTool(handler).CallAsync(
            Token, Args("""{"path":"D:\\src\\r","files":["C:\\x\\a.cs"]}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(@"D:\src\r", Assert.Single(handler.Calls).Request.Directory);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData(@"C:\a:b")]
    public async Task Голый_диск_и_двоеточие_после_корня_отклоняются_обоими_инструментами(string path)
    {
        var file = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));
        var diff = new RecordingShowDiffHandler(new ShowDiffOutcome.Shown("ok"));

        var results = new[]
        {
            await new ShowFileTool(file).CallAsync(Token, Json(new { files = new[] { path } }), CancellationToken.None),
            await new ShowFileTool(file).CallAsync(Token, Json(new { path, files = new[] { "a.cs" } }), CancellationToken.None),
            await new ShowDiffTool(diff).CallAsync(Token, Json(new { files = new[] { path } }), CancellationToken.None),
            await new ShowDiffTool(diff).CallAsync(Token, Json(new { path }), CancellationToken.None),
        };

        Assert.All(results, static result =>
        {
            Assert.True(result.IsError);
            Assert.Contains("Only local paths are allowed", result.Text, StringComparison.Ordinal);
        });
        Assert.Empty(file.Calls);
        Assert.Empty(diff.Calls);
    }

    /// <summary>
    /// Разрешённый набор. Пробел в начале срезается разбором аргументов: до приложения путь
    /// доходит без него (текущее поведение).
    /// </summary>
    [Theory]
    [InlineData("C:/x", "C:/x")]
    [InlineData(@"C:\x", @"C:\x")]
    [InlineData("src/a.cs", "src/a.cs")]
    [InlineData(" src/a.cs", "src/a.cs")]
    [InlineData(@" C:\x", @"C:\x")]
    public async Task Локальные_пути_проходят_в_обоих_инструментах(string path, string expected)
    {
        var file = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));
        var diff = new RecordingShowDiffHandler(new ShowDiffOutcome.Shown("ok"));

        var asFile = await new ShowFileTool(file).CallAsync(Token, Json(new { files = new[] { path } }), CancellationToken.None);
        var asFileDirectory = await new ShowFileTool(file).CallAsync(Token, Json(new { path, files = new[] { "a.cs" } }), CancellationToken.None);
        var asDiffFile = await new ShowDiffTool(diff).CallAsync(Token, Json(new { files = new[] { path } }), CancellationToken.None);
        var asDiffDirectory = await new ShowDiffTool(diff).CallAsync(Token, Json(new { path }), CancellationToken.None);

        Assert.False(asFile.IsError);
        Assert.False(asFileDirectory.IsError);
        Assert.False(asDiffFile.IsError);
        Assert.False(asDiffDirectory.IsError);
        Assert.Equal(expected, file.Calls[0].Request.Files[0].Path);
        Assert.Equal(expected, file.Calls[1].Request.Directory);
        Assert.Equal(expected, diff.Calls[0].Request.Files[0]);
        Assert.Equal(expected, diff.Calls[1].Request.Directory);
    }

    [Fact]
    public async Task End_line_без_start_line_объясняется_агенту()
    {
        var tool = new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok")));

        var result = await tool.CallAsync(Token, Args("""{"files":[{"path":"a.cs","end_line":3}]}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'end_line' requires 'start_line'", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Больше_потолка_файлов_ошибка_с_пределом()
    {
        var handler = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));
        var tool = new ShowFileTool(handler);
        var files = string.Join(",", Enumerable.Range(0, ShowFileTool.MaxFiles + 1).Select(static i => $"\"f{i}.cs\""));

        var result = await tool.CallAsync(Token, Args("{\"files\":[" + files + "]}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains($"at most {ShowFileTool.MaxFiles}", result.Text, StringComparison.Ordinal);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Ровно_потолок_файлов_проходит()
    {
        var handler = new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"));
        var tool = new ShowFileTool(handler);
        var files = string.Join(",", Enumerable.Range(0, ShowFileTool.MaxFiles).Select(static i => $"\"f{i}.cs\""));

        var result = await tool.CallAsync(Token, Args("{\"files\":[" + files + "]}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(ShowFileTool.MaxFiles, Assert.Single(handler.Calls).Request.Files.Count);
    }

    [Fact]
    public async Task Исходы_приложения_становятся_ответом_агенту()
    {
        var unknown = await new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.UnknownSession()))
            .CallAsync(null, Args("""{"files":["a.cs"]}"""), CancellationToken.None);
        var failed = await new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.Failed("No such directory")))
            .CallAsync(Token, Args("""{"files":["a.cs"]}"""), CancellationToken.None);
        var broken = await new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"), new InvalidOperationException("boom")))
            .CallAsync(Token, Args("""{"files":["a.cs"]}"""), CancellationToken.None);

        Assert.True(unknown.IsError);
        Assert.Contains("No Agents Shell tab", unknown.Text, StringComparison.Ordinal);
        Assert.Equal(McpToolResult.Error("No such directory"), failed);
        Assert.True(broken.IsError);
        Assert.DoesNotContain("boom", broken.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Отмена_приёмника_не_превращается_в_ошибку_инструмента()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var tool = new ShowFileTool(new RecordingShowFileHandler(
            new ShowFileOutcome.Shown("ok"), new OperationCanceledException(cancellation.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallAsync(Token, Args("""{"files":["a.cs"]}"""), cancellation.Token));
    }

    [Fact]
    public async Task Tools_list_содержит_оба_инструмента()
    {
        var handler = new McpJsonRpcHandler(
        [
            new ShowDiffTool(new RecordingShowDiffHandler(new ShowDiffOutcome.Shown("ok"))),
            new ShowFileTool(new RecordingShowFileHandler(new ShowFileOutcome.Shown("ok"))),
        ]);

        var reply = await handler.HandleAsync(Token, """{"method":"tools/list","jsonrpc":"2.0","id":1}""", CancellationToken.None);

        using var document = JsonDocument.Parse(reply.Body!);
        Assert.Equal(
            ["show_diff", "show_file"],
            document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(static t => t.GetProperty("name").GetString()));
    }

    private static JsonElement? Json(object value) => Args(JsonSerializer.Serialize(value));

    private static JsonElement? Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal sealed class RecordingShowFileHandler(ShowFileOutcome outcome, Exception? failure = null) : IShowFileHandler
{
    private readonly List<(string? Token, ShowFileRequest Request)> _calls = [];

    public IReadOnlyList<(string? Token, ShowFileRequest Request)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ShowFileOutcome> HandleAsync(string? correlationToken, ShowFileRequest request, CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((correlationToken, request));
        }

        return failure is not null ? Task.FromException<ShowFileOutcome>(failure) : Task.FromResult(outcome);
    }
}
