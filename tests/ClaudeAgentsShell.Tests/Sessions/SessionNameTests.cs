using System.Text;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Sessions.History;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Имя сессии, которое даёт сам Claude Code: записи <c>custom-title</c> (<c>/rename</c>) и
/// <c>ai-title</c> в транскрипте, обе «последняя побеждает», и чтение по <c>transcript_path</c>.
/// </summary>
public sealed class SessionNameTests
{
    private const string WorkingDirectory = @"D:\src\domovoy";
    private const string SessionId = "9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f";
    private const string FirstMessage = """{"type":"user","message":{"role":"user","content":"почини сборку"}}""";

    [Fact]
    public async Task Без_записей_имени_показывается_первое_сообщение()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, Assistant("готово"));

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Null(summary?.Name);
        Assert.Equal("почини сборку", summary?.DisplayTitle);
    }

    [Fact]
    public async Task Последняя_ai_title_побеждает()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, AiTitle("Первое имя"), Assistant("…"), AiTitle("Второе имя"));

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("Второе имя", summary?.Name);
        Assert.Equal("Второе имя", summary?.DisplayTitle);
        Assert.Equal("почини сборку", summary?.Title);
    }

    [Fact]
    public async Task Custom_title_важнее_ai_title_даже_более_поздней()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, CustomTitle("мой релиз"), AiTitle("Починка сборки"));

        Assert.Equal("мой релиз", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Пустой_custom_title_сбрасывает_имя_к_ai_title()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, AiTitle("Починка сборки"), CustomTitle("мой релиз"), CustomTitle(""));

        Assert.Equal("Починка сборки", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Сброшенное_имя_без_ai_title_возвращает_первое_сообщение()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, CustomTitle("мой релиз"), CustomTitle("   "));

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Null(summary?.Name);
        Assert.Equal("почини сборку", summary?.DisplayTitle);
    }

    [Fact]
    public async Task Метка_внутри_текста_и_вложенного_объекта_именем_не_считается()
    {
        // В строковом значении кавычки экранированы; вложенный объект с тем же type — не корень.
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            FirstMessage,
            """{"type":"user","message":{"role":"user","content":"{\"type\":\"ai-title\",\"aiTitle\":\"подделка\"}"}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","input":{"type":"ai-title","aiTitle":"вложенное"}}]}}""",
            """{"type":"ai-title","aiTitle":""",
            """not json "type":"ai-title" at all""");

        Assert.Null((await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Имя_длинной_строкой_схлопывается_как_заголовок()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, AiTitle("Починка\\n  сборки " + new string('x', 300)));

        var name = (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name;

        Assert.NotNull(name);
        Assert.StartsWith("Починка сборки x", name);
        Assert.DoesNotContain('\n', name);
        Assert.True(name.Length <= 120);
    }

    [Fact]
    public async Task Имя_дописанное_в_живой_транскрипт_подхватывается()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, FirstMessage);

        Assert.Null((await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        AppendLines(path, Assistant("…"), AiTitle("Починка сборки"));
        Assert.Equal("Починка сборки", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        AppendLines(path, CustomTitle("мой релиз"));
        Assert.Equal("мой релиз", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        // Свежая ai-title, дописанная после /rename, действующее имя не перебивает.
        AppendLines(path, AiTitle("Починка сборки 2"));
        Assert.Equal("мой релиз", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        AppendLines(path, CustomTitle(""));
        Assert.Equal("Починка сборки 2", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Имя_найденное_раньше_переносится_когда_дописанное_его_не_несёт()
    {
        // Дописанного больше окна хвоста: найденное в прошлый раз не теряется.
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, FirstMessage, CustomTitle("мой релиз"));

        Assert.Equal("мой релиз", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        AppendLines(path, Enumerable.Repeat(Assistant(new string('ы', 1000)), 1200).ToArray());

        Assert.Equal("мой релиз", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task У_дописанного_файла_начало_не_разбирается_заново()
    {
        // Строка заголовка на месте и не менялась — начало файла не перечитывается. Проверка через
        // подмену строки до заголовка той же длины: разбор с нуля нашёл бы в ней новый заголовок.
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var early = """{"type":"system","content":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""";
        var path = WriteTranscript(temp, SessionId, early, FirstMessage);

        Assert.Equal("почини сборку", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        var replaced = """{"type":"user","message":{"role":"user","content":"fake"}}""";
        var bytes = File.ReadAllBytes(path);
        var head = Encoding.UTF8.GetBytes(replaced.PadRight(Encoding.UTF8.GetByteCount(early)));
        head.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        AppendLines(path, Assistant("…"), AiTitle("Починка сборки"));

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("почини сборку", summary?.Title);
        Assert.Equal("Починка сборки", summary?.Name);
    }

    [Fact]
    public async Task Недописанная_последняя_строка_ждёт_конца()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, FirstMessage, AiTitle("Старое"));

        Assert.Equal("Старое", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        var line = AiTitle("Новое");
        AppendRaw(path, line[..10]);
        Assert.Equal("Старое", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);

        AppendRaw(path, line[10..] + "\n");
        Assert.Equal("Новое", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Имя_глубже_хвоста_в_списке_не_ищется()
    {
        // Список читает только хвост: запись имени, лежащая дальше, не найдётся, и строка
        // деградирует к первому сообщению. У законченной сессии имя всегда у конца (замер).
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var filler = Enumerable.Repeat(Assistant(new string('z', 1000)), 100).ToArray();
        WriteTranscript(temp, SessionId, [FirstMessage, AiTitle("Глубоко"), .. filler]);

        var summary = Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None));

        Assert.Null(summary.Name);
        Assert.Equal("почини сборку", summary.DisplayTitle);
    }

    [Fact]
    public async Task Список_истории_несёт_имя_сессии()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId, FirstMessage, Assistant("…"), AiTitle("Починка сборки"));

        var summary = Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None));

        Assert.Equal("Починка сборки", summary.Name);
    }

    [Fact]
    public async Task По_пути_из_хука_читается_транскрипт_в_другом_каталоге()
    {
        // После cd или перехода в worktree транскрипт лежит в slug другого каталога.
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscriptAt(temp, @"D:\src\domovoy-worktree", SessionId, FirstMessage, AiTitle("Из worktree"));

        var summary = await reader.ReadTranscriptAsync(path, SessionId, CancellationToken.None);

        Assert.Equal("Из worktree", summary?.Name);
        Assert.Null(await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Путь_вне_каталога_Claude_Code_не_читается()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var outside = temp.Combine("elsewhere", SessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, FirstMessage + "\n", new UTF8Encoding(false));

        Assert.Null(await reader.ReadTranscriptAsync(outside, SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Путь_с_переходом_вверх_из_каталога_не_выводит()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var outside = temp.Combine("claude", SessionId + ".jsonl");
        Directory.CreateDirectory(temp.Combine("claude", "projects", "slug"));
        File.WriteAllText(outside, FirstMessage + "\n", new UTF8Encoding(false));

        var sneaky = temp.Combine("claude", "projects", "slug", "..", "..", SessionId + ".jsonl");

        Assert.Null(await reader.ReadTranscriptAsync(sneaky, SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Соседний_каталог_с_тем_же_началом_имени_не_считается_своим()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var sibling = temp.Combine("claude", "projects2", "slug", SessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, FirstMessage + "\n", new UTF8Encoding(false));

        Assert.Null(await reader.ReadTranscriptAsync(sibling, SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Путь_к_чужой_сессии_или_не_транскрипту_не_читается()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, FirstMessage);

        Assert.Null(await reader.ReadTranscriptAsync(path, "00000000-0000-0000-0000-000000000000", CancellationToken.None));
        Assert.Null(await reader.ReadTranscriptAsync(Path.ChangeExtension(path, ".json"), SessionId, CancellationToken.None));
        Assert.Null(await reader.ReadTranscriptAsync(Path.GetFileName(path), SessionId, CancellationToken.None));
        Assert.Null(await reader.ReadTranscriptAsync(path, "../" + SessionId, CancellationToken.None));
        Assert.Null(await reader.ReadTranscriptAsync("", SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Путь_к_ещё_не_созданному_файлу_даёт_null()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = Path.Combine(temp.Combine("claude", "projects"), "slug", SessionId + ".jsonl");

        Assert.Null(await reader.ReadTranscriptAsync(path, SessionId, CancellationToken.None));
    }

    private static string AiTitle(string title) =>
        "{\"type\":\"ai-title\",\"aiTitle\":\"" + title + "\",\"sessionId\":\"" + SessionId + "\"}";

    private static string CustomTitle(string title) =>
        "{\"type\":\"custom-title\",\"customTitle\":\"" + title + "\",\"sessionId\":\"" + SessionId + "\"}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"" + text + "\"}}";

    private static void AppendLines(string path, params string[] lines) =>
        AppendRaw(path, string.Join('\n', lines) + "\n");

    private static void AppendRaw(string path, string text)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, text, new UTF8Encoding(false));

        // Время изменения явно вперёд: на грубом таймере файловой системы оно могло бы совпасть.
        File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));
    }

    private static SessionHistoryReader CreateReader(TempDirectory temp) =>
        new(new AppDataPaths(temp.Combine("appdata"), temp.Combine("claude", "projects")), new SessionsOptions());

    private static string WriteTranscript(TempDirectory temp, string sessionId, params string[] lines) =>
        WriteTranscriptAt(temp, WorkingDirectory, sessionId, lines);

    private static string WriteTranscriptAt(TempDirectory temp, string workingDirectory, string sessionId, params string[] lines)
    {
        var directory = Path.Combine(temp.Combine("claude", "projects"), SessionSlug.From(workingDirectory));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        return path;
    }
}
