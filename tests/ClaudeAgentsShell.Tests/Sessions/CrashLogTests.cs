using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Журнал сбоев. Проверяется то, ради чего он и заведён: запись доезжает до файла целиком,
/// файл не растёт без предела, а сам журнал не падает — иначе он добивал бы приложение
/// вместо того, чтобы объяснить его падение.
/// </summary>
public sealed class CrashLogTests
{
    private static CrashLog Create(TempDirectory temp, string? appData = null) =>
        new(new AppDataPaths(appData ?? temp.Combine("appdata"), temp.Combine("claude")), TimeProvider.System);

    [Fact]
    public void Запись_создаёт_файл_в_каталоге_данных()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var log = Create(temp, appData);

        var path = log.Write("TestSource", Thrown(new InvalidOperationException("сломалось")));

        Assert.Equal(Path.Combine(appData, CrashLog.FileName), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void В_записи_есть_источник_тип_сообщение_и_стек()
    {
        using var temp = new TempDirectory();
        var log = Create(temp);

        var path = log.Write("DispatcherUnhandledException", Thrown(new InvalidOperationException("сломалось")));

        var text = File.ReadAllText(path!);
        Assert.Contains("DispatcherUnhandledException", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("сломалось", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Внутреннее_исключение_попадает_в_запись()
    {
        using var temp = new TempDirectory();
        var log = Create(temp);
        var inner = Thrown(new IOException("диск занят"));

        var path = log.Write("TestSource", Thrown(new InvalidOperationException("обёртка", inner)));

        var text = File.ReadAllText(path!);
        Assert.Contains("обёртка", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", text, StringComparison.Ordinal);
        Assert.Contains("диск занят", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Все_причины_агрегата_попадают_в_запись()
    {
        using var temp = new TempDirectory();
        var log = Create(temp);
        var aggregate = new AggregateException(
            Thrown(new InvalidOperationException("первая")),
            Thrown(new TimeoutException("вторая")));

        var path = log.Write("TaskScheduler.UnobservedTaskException", aggregate);

        var text = File.ReadAllText(path!);
        Assert.Contains("первая", text, StringComparison.Ordinal);
        Assert.Contains("вторая", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Вторая_запись_дописывается_к_первой()
    {
        using var temp = new TempDirectory();
        var log = Create(temp);

        log.Write("TestSource", Thrown(new InvalidOperationException("первый сбой")));
        var path = log.Write("TestSource", Thrown(new InvalidOperationException("второй сбой")));

        var text = File.ReadAllText(path!);
        Assert.Contains("первый сбой", text, StringComparison.Ordinal);
        Assert.Contains("второй сбой", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Переполненный_файл_уезжает_в_предыдущий_и_журнал_начинается_заново()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var log = Create(temp, appData);

        // Одно исключение с огромным сообщением дешевле тысячи мелких записей и даёт
        // ровно ту же ситуацию: файл вырос выше потолка.
        var huge = new string('я', (int)CrashLog.MaxBytes);
        log.Write("TestSource", Thrown(new InvalidOperationException(huge)));
        log.Write("TestSource", Thrown(new InvalidOperationException("после ротации")));

        var current = Path.Combine(appData, CrashLog.FileName);
        var previous = Path.Combine(appData, CrashLog.PreviousFileName);

        Assert.True(File.Exists(previous));
        Assert.True(new FileInfo(previous).Length >= CrashLog.MaxBytes);
        Assert.True(new FileInfo(current).Length < CrashLog.MaxBytes);
        Assert.Contains("после ротации", File.ReadAllText(current), StringComparison.Ordinal);
    }

    [Fact]
    public void Недоступный_каталог_не_роняет_запись()
    {
        using var temp = new TempDirectory();

        // На месте каталога данных лежит файл: создать каталог не выйдет ни при какой
        // попытке. Это и есть «журналу некуда писать».
        var occupied = temp.Combine("appdata");
        File.WriteAllText(occupied, "занято");

        var log = Create(temp, occupied);

        var path = log.Write("TestSource", Thrown(new InvalidOperationException("сломалось")));

        Assert.Null(path);
    }

    [Fact]
    public void Бесконечная_цепочка_причин_обрезается_по_глубине()
    {
        using var temp = new TempDirectory();
        var log = Create(temp);

        Exception chain = new InvalidOperationException("самая глубокая");
        for (var i = 0; i < 20; i++)
        {
            chain = new InvalidOperationException("уровень " + i, chain);
        }

        var path = log.Write("TestSource", chain);

        Assert.NotNull(path);
        Assert.Contains("обрезана по глубине", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    /// <summary>Брошенное и пойманное исключение: только у такого есть стек.</summary>
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
