using System.Text;
using System.Text.Json;
using ClaudeAgentsShell.Sessions;
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
    public async Task Служебный_вывод_заголовком_не_становится()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"служебное"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>вывод команды</local-command-stdout>"}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"задание сабагенту"}}""",
            """{"type":"user","message":{"role":"user","content":"  настоящий\n  вопрос  "}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("настоящий вопрос", summary?.Title);
    }

    [Fact]
    public async Task Слэш_команда_становится_заголовком_вместе_с_аргументами()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"<command-message>work</command-message>\n<command-name>/work</command-name>\n<command-args>WO-15917 препрод</command-args>"}}""",
            """{"type":"user","message":{"role":"user","content":"следующий вопрос"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("/work WO-15917 препрод", summary?.Title);
    }

    [Fact]
    public async Task Слэш_команда_без_аргументов_даёт_заголовком_саму_команду()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"<command-name>/init</command-name>\n<command-message>init</command-message>\n<command-args></command-args>"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("/init", summary?.Title);
    }

    [Fact]
    public async Task Команда_самой_оболочки_заголовком_не_становится()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);

        // У /clear нет origin: агенту она не отправляется. Настоящее первое сообщение — следующее.
        WriteTranscript(temp, SessionId,
            """{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>\n<command-message>clear</command-message>\n<command-args></command-args>"}}""",
            """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"настоящий вопрос"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("настоящий вопрос", summary?.Title);
    }

    [Theory]
    // Незакрытый тег.
    [InlineData("<command-name>/work")]
    // Закрывающий тег без открывающего.
    [InlineData("<command-args>WO-1</command-args>/work</command-name>")]
    // Пустое содержимое команды.
    [InlineData("<command-name></command-name><command-args>WO-1</command-args>")]
    // Одни пробелы внутри.
    [InlineData("<command-name>   </command-name>")]
    // Вложенность: содержимое начинается не со слэша, значит это не имя команды.
    [InlineData("<command-name><command-name>/work</command-name></command-name>")]
    // Мусор вместо обёртки.
    [InlineData("<<<>>><command-name")]
    public async Task Битая_обёртка_команды_разбор_не_роняет(string content)
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var command = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"""
            + JsonSerializer.Serialize(content) + "}}";
        WriteTranscript(temp, SessionId,
            command,
            """{"type":"user","message":{"role":"user","content":"следующий вопрос"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("следующий вопрос", summary?.Title);
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

    [Fact]
    public async Task Предел_просмотра_обрывает_чтение_и_даёт_сводку_без_заголовка()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp, new SessionsOptions { TranscriptScanLimit = 200 });
        WriteTranscript(temp, SessionId,
            Filler,
            Filler,
            """{"type":"user","message":{"role":"user","content":"слишком глубоко"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        // Сводка есть, заголовка нет: это ответ «прочитали и не нашли», по которому
        // вызывающий перестаёт спрашивать (а не «файла нет», после которого спросит снова).
        Assert.NotNull(summary);
        Assert.Null(summary.Title);
        Assert.Equal(SessionId, summary.SessionId);
    }

    [Fact]
    public async Task Заголовок_в_пределах_предела_по_прежнему_находится()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp, new SessionsOptions { TranscriptScanLimit = 64 * 1024 });
        WriteTranscript(temp, SessionId,
            Filler,
            Filler,
            """{"type":"user","message":{"role":"user","content":"вопрос в пределах предела"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("вопрос в пределах предела", summary?.Title);
    }

    [Fact]
    public async Task Предел_просмотра_действует_и_на_список()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp, new SessionsOptions { TranscriptScanLimit = 200 });
        WriteTranscript(temp, SessionId,
            Filler,
            Filler,
            """{"type":"user","message":{"role":"user","content":"слишком глубоко"}}""");

        // Список деградирует до «имя файла и дата» — строка остаётся, заголовка в ней нет.
        var summaries = await reader.ReadAsync(WorkingDirectory, CancellationToken.None);

        Assert.Null(Assert.Single(summaries).Title);
    }

    /// <summary>Строка, которая заголовком стать не может, — около 140 символов.</summary>
    private static string Filler => "{\"type\":\"assistant\",\"text\":\"" + new string('a', 110) + "\"}";

    [Fact]
    public async Task Сотня_транскриптов_читается_по_порядку_а_повторно_без_открытия_файлов()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var paths = new List<string>();
        for (var index = 0; index < 100; index++)
        {
            var path = WriteTranscript(temp, $"session-{index:D3}",
                """{"type":"queue-operation","operation":"enqueue"}""",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"вопрос " + index + "\"}}");
            File.SetLastWriteTimeUtc(path, start.AddMinutes(index));
            paths.Add(path);
        }

        var first = await reader.ReadAsync(WorkingDirectory, CancellationToken.None);

        Assert.Equal(100, first.Count);
        Assert.Equal("session-099", first[0].SessionId);
        Assert.Equal("session-000", first[^1].SessionId);
        Assert.All(first, summary => Assert.Equal("вопрос " + int.Parse(summary.SessionId[^3..]), summary.Title));

        // Файлы заперты целиком: открыть их нельзя, а перечисление каталога работает. Если бы
        // повторный вызов открывал неизменённые транскрипты, заголовки деградировали бы до null.
        var locks = paths.Select(static path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var second = await reader.ReadAsync(WorkingDirectory, CancellationToken.None);

            Assert.Equal(first.Select(static s => s.SessionId), second.Select(static s => s.SessionId));
            Assert.All(second, summary => Assert.NotNull(summary.Title));
        }
        finally
        {
            foreach (var stream in locks)
            {
                stream.Dispose();
            }
        }
    }

    [Fact]
    public async Task Удалённый_транскрипт_вычищается_из_кэша()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var path = WriteTranscript(temp, SessionId, """{"type":"user","message":{"role":"user","content":"первый"}}""");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("первый", Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);

        File.Delete(path);
        Assert.Empty(await reader.ReadAsync(WorkingDirectory, CancellationToken.None));

        // Тот же путь, то же время и тот же размер: живи запись в кэше, вернулся бы старый заголовок.
        WriteTranscript(temp, SessionId, """{"type":"user","message":{"role":"user","content":"второй"}}""");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("второй", Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);
    }

    [Fact]
    public async Task Не_открывшийся_транскрипт_перечитывается_позже()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, """{"type":"user","message":{"role":"user","content":"вопрос"}}""");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Занят — строка деградирует до «имя файла и дата».
            Assert.Null(Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);
        }

        Assert.Equal("вопрос", Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);
    }

    [Fact]
    public async Task Дозапись_в_транскрипт_без_заголовка_не_перечитывает_начало()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId, Filler, Assistant("два"));

        Assert.Null((await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        // Начало подменено на строку с заголовком той же длины. Перечитай его чтение с нуля —
        // заголовок нашёлся бы; продолжение с сохранённой позиции его не видит.
        OverwriteHead(path, "подменённое начало");
        AppendLines(path, Assistant("три"));

        var afterAppend = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);
        Assert.NotNull(afterAppend);
        Assert.Null(afterAppend.Title);

        // Заголовок в дописанной части в пределах лимита находится.
        AppendLines(path, """{"type":"user","message":{"role":"user","content":"дописанный вопрос"}}""");

        var withTitle = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);
        Assert.Equal("дописанный вопрос", withTitle?.Title);
        Assert.Equal(new FileInfo(path).Length, withTitle?.SizeBytes);
    }

    [Fact]
    public async Task Упёршийся_в_предел_транскрипт_при_росте_не_сканируется()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp, new SessionsOptions { TranscriptScanLimit = 200 });
        var path = WriteTranscript(temp, SessionId, Filler, Filler);

        Assert.Null(Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);

        OverwriteHead(path, "подменённое начало");
        AppendLines(path, Assistant("ещё"));

        var grown = Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None));
        Assert.Null(grown.Title);
        Assert.Equal(new FileInfo(path).Length, grown.SizeBytes);
    }

    [Fact]
    public async Task Урезанный_транскрипт_сканируется_заново()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp, new SessionsOptions { TranscriptScanLimit = 200 });
        var path = WriteTranscript(temp, SessionId, Filler, Filler, Filler);

        Assert.Null(Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);

        // Файл стал короче — это уже другой файл, сохранённая позиция к нему не относится.
        OverwriteHead(path, "новое начало");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(Encoding.UTF8.GetByteCount(Filler) + 1);
        }

        Assert.Equal("новое начало", Assert.Single(await reader.ReadAsync(WorkingDirectory, CancellationToken.None)).Title);
    }

    [Theory]
    [InlineData(20 * 1024)]
    [InlineData(100 * 1024)]
    public async Task Строка_длиннее_буфера_чтения_не_мешает_заголовку_после_неё(int length)
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        WriteTranscript(temp, SessionId,
            Assistant(new string('a', length)),
            """{"type":"user","message":{"role":"user","content":"после длинной строки"}}""");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("после длинной строки", summary?.Title);
    }

    [Fact]
    public async Task Длинная_строка_с_заголовком_читается_целиком()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var question = "вопрос " + new string('б', 30 * 1024);
        WriteTranscript(temp, SessionId,
            Assistant("до"),
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + question + "\"}}");

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        // Парсер может укоротить заголовок, но начало обязано совпасть и не быть испорченным.
        Assert.NotNull(summary?.Title);
        Assert.StartsWith("вопрос ббб", summary.Title);
        Assert.DoesNotContain('�', summary.Title);
    }

    [Fact]
    public async Task Кириллица_поперёк_границы_буфера_не_портится()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);

        // Буфер чтения — 16384 байта. Первая строка подобрана так, чтобы граница пришлась ровно
        // на середину двухбайтовой «Ж» в заголовке второй строки.
        const int boundary = 16 * 1024;
        const string titlePrefix = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"";
        var fillerTemplate = Assistant(string.Empty);
        var fillerLength = boundary - 1 - Encoding.UTF8.GetByteCount(titlePrefix) - 1;
        var filler = Assistant(new string('a', fillerLength - Encoding.UTF8.GetByteCount(fillerTemplate)));
        var path = WriteTranscript(temp, SessionId, filler, titlePrefix + "Жук на границе\"}}");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xD0, bytes[boundary - 1]);
        Assert.Equal(0x96, bytes[boundary]);

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("Жук на границе", summary?.Title);
    }

    [Fact]
    public async Task Метка_порядка_байтов_в_начале_не_мешает_разбору()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId);
        File.WriteAllText(
            path,
            """{"type":"user","message":{"role":"user","content":"с меткой"}}""" + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal("с меткой", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);
    }

    [Fact]
    public async Task Переводы_строк_windows_разбираются()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId);
        File.WriteAllText(
            path,
            Assistant("до") + "\r\n" + """{"type":"user","gitBranch":"main","message":{"role":"user","content":"виндовый"}}""" + "\r\n",
            new UTF8Encoding(false));

        var summary = await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None);

        Assert.Equal("виндовый", summary?.Title);
        Assert.Equal("main", summary?.Branch);
    }

    [Fact]
    public async Task Незаконченная_строка_после_дозаписи_читается_целиком()
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var path = WriteTranscript(temp, SessionId);
        File.WriteAllText(
            path,
            Filler + "\n" + """{"type":"user","message":{"role":"user","content":"нач""",
            new UTF8Encoding(false));

        // Строка ещё пишется — разобрать её нельзя, но и в просмотренное она не засчитана.
        Assert.Null((await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        var modified = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, "ало\"}}\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));

        Assert.Equal("начало", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Время_изменения_назад_сканирует_с_нуля(bool grow)
    {
        using var temp = new TempDirectory();
        var reader = CreateReader(temp);
        var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var path = WriteTranscript(temp, SessionId, Filler, Assistant("без заголовка"));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Null((await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);

        // Подмена начала видна только при скане с нуля: продолжение с позиции её бы пропустило.
        OverwriteHead(path, "другой файл");
        if (grow)
        {
            File.AppendAllText(path, Assistant("ещё") + "\n", new UTF8Encoding(false));
        }

        File.SetLastWriteTimeUtc(path, stamp.AddHours(-1));

        Assert.Equal("другой файл", (await reader.ReadOneAsync(WorkingDirectory, SessionId, CancellationToken.None))?.Title);
    }

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"" + text + "\"}}";

    /// <summary>
    /// Заменяет первую строку файла строкой пользователя с заголовком <paramref name="title" />
    /// той же длины в байтах: размер файла не меняется.
    /// </summary>
    private static void OverwriteHead(string path, string title)
    {
        var bytes = File.ReadAllBytes(path);
        var firstLength = Array.IndexOf(bytes, (byte)'\n');

        var prefix = "{\"type\":\"user\",\"pad\":\"";
        var suffix = "\",\"message\":{\"role\":\"user\",\"content\":\"" + title + "\"}}";
        var padding = firstLength - Encoding.UTF8.GetByteCount(prefix + suffix);
        Assert.True(padding >= 0, "Первая строка слишком коротка для подмены.");

        var head = Encoding.UTF8.GetBytes(prefix + new string('x', padding) + suffix);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.Write(head);
    }

    private static void AppendLines(string path, params string[] lines)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        // Время изменения явно вперёд: на грубом таймере файловой системы оно могло бы совпасть.
        File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));
    }

    private static SessionHistoryReader CreateReader(TempDirectory temp, SessionsOptions? options = null) =>
        new(new AppDataPaths(temp.Combine("appdata"), temp.Combine("claude", "projects")), options ?? new SessionsOptions());

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
