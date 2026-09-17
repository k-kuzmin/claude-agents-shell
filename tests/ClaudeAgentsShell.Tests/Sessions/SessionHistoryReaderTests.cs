using System.Text;
using ClaudeAgentsShell.Sessions.History;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class SessionHistoryReaderTests
{
    private const string WorkingDirectory = @"D:\src\domovoy";
    private const string SessionId = "9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f";

    [Fact]
    public async Task Отсутствие_каталога_даёт_пустую_историю()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);

        Assert.Empty(await reader.ReadAsync(WorkingDirectory, CancellationToken.None));
        Assert.Null(await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Заголовок_берётся_из_первого_сообщения_пользователя()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"queue-operation","operation":"enqueue"}""",
            """{"type":"user","gitBranch":"stage/m4-hooks","message":{"role":"user","content":"Почини наблюдатель ветки"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":"Хорошо"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal("Почини наблюдатель ветки", summary.Title);
        Assert.Equal("stage/m4-hooks", summary.Branch);
        Assert.Equal(SessionId, summary.SessionId);
        Assert.Null(summary.MessageCount);
    }

    [Fact]
    public async Task Сообщение_блоками_тоже_даёт_заголовок()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Собери проект"}]}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("Собери проект", summary?.Title);
    }

    [Fact]
    public async Task Служебные_строки_заголовком_не_становятся()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"служебное"}}""",
            """{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"задание сабагенту"}}""",
            """{"type":"user","message":{"role":"user","content":"  настоящий\n  вопрос  "}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("настоящий вопрос", summary?.Title);
    }

    [Fact]
    public async Task Битые_строки_пропускаются_разбор_продолжается()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            "{ это не json",
            "[1, 2, 3]",
            string.Empty,
            """{"type":"user","message":{"role":"user","content":"после мусора"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("после мусора", summary?.Title);
    }

    [Fact]
    public async Task Пустой_и_неразобравшийся_файл_деградируют_до_имени_и_даты()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId);
        var broken = "11111111-2222-3333-4444-555555555555";
        WriteTranscript(temp, broken, "не json ни в одной строке", "и тут тоже");

        var empty = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);
        var unreadable = await reader.ReadOneAsync(WorkingDirectory, broken, CancellationToken.None);

        Assert.NotNull(empty);
        Assert.Null(empty.Title);
        Assert.Equal(0, empty.SizeBytes);

        Assert.NotNull(unreadable);
        Assert.Null(unreadable.Title);
        Assert.True(unreadable.SizeBytes > 0);
        Assert.NotEqual(default, unreadable.ModifiedUtc);
    }

    [Fact]
    public async Task Чтение_останавливается_на_заголовке()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","message":{"role":"user","content":"первый вопрос"}}""",
            """{"type":"user","gitBranch":"строка-после-заголовка","message":{"role":"user","content":"второй вопрос"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("первый вопрос", summary?.Title);

        // Ветка со строки, лежащей ниже заголовка, не подхватилась — значит файл дальше не читали.
        Assert.Null(summary?.Branch);
    }

    [Fact]
    public async Task Кэш_не_перечитывает_файл_с_прежними_временем_и_размером()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId,
            """{"type":"user","message":{"role":"user","content":"первый"}}""");

        var before = new FileInfo(path);
        var originalLength = before.Length;
        var originalWrite = before.LastWriteTimeUtc;

        Assert.Equal("первый", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        // Тот же размер и то же время изменения: для кэша файл «не менялся».
        await File.WriteAllTextAsync(
            path,
            """{"type":"user","message":{"role":"user","content":"второй"}}""" + "\n",
            new UTF8Encoding(false),
            CancellationToken.None);
        File.SetLastWriteTimeUtc(path, originalWrite);
        Assert.Equal(originalLength, new FileInfo(path).Length);

        Assert.Equal("первый", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        // Размер изменился — кэш обязан перечитать.
        await File.AppendAllTextAsync(path, "\n", CancellationToken.None);

        Assert.Equal("второй", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);
    }

    [Fact]
    public async Task Список_сессий_идёт_от_свежих_к_старым()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var older = WriteTranscript(temp, "11111111-1111-1111-1111-111111111111",
            """{"type":"user","message":{"role":"user","content":"старая"}}""");
        var newer = WriteTranscript(temp, "22222222-2222-2222-2222-222222222222",
            """{"type":"user","message":{"role":"user","content":"свежая"}}""");

        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var summaries = await reader.ReadAsync(WorkingDirectory, CancellationToken.None);

        Assert.Equal(["свежая", "старая"], summaries.Select(static s => s.Title));
    }

    [Theory]
    [InlineData(@"..\..\чужой")]
    [InlineData("../../другой")]
    [InlineData(@"C:\Windows\System32\config")]
    [InlineData("")]
    public async Task Идентификатор_сессии_из_хука_не_уводит_чтение_из_каталога(string sessionId)
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","message":{"role":"user","content":"своё"}}""");

        Assert.Null(await reader.ReadOneAsync(WorkingDirectory, sessionId, CancellationToken.None));
    }

    private static SessionHistoryReader CreateReader(TempDirectory temp) =>
        new(new AppDataPaths(temp.Combine("appdata"), temp.Combine("claude", "projects")));

    /// <summary>Кладёт транскрипт туда, где его ищет Claude Code: <c>&lt;projects&gt;/&lt;slug&gt;</c>.</summary>
    private static string WriteTranscript(TempDirectory temp, string sessionId, params string[] lines)
    {
        var directory = Path.Combine(temp.Combine("claude", "projects"), SessionSlug.From(WorkingDirectory));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(path, lines.Length == 0 ? string.Empty : string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        return path;
    }
}
