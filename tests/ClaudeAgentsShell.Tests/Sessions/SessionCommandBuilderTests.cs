using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Launch;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class SessionCommandBuilderTests
{
    private readonly SessionCommandBuilder _builder = new();

    [Fact]
    public void Новая_сессия_это_просто_claude()
    {
        var lines = _builder.Build(Project(), new SessionLaunch.NewSession(), null);

        Assert.Equal(["claude\r"], lines);
    }

    [Fact]
    public void Дополнительные_аргументы_идут_за_командой()
    {
        var lines = _builder.Build(Project(extraArgs: ["--model", "opus"]), new SessionLaunch.NewSession(), null);

        Assert.Equal(["claude --model opus\r"], lines);
    }

    [Fact]
    public void Продолжение_конкретной_сессии_это_resume()
    {
        var lines = _builder.Build(Project(), new SessionLaunch.ResumeSession("9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f"), null);

        Assert.Equal(["claude --resume 9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f\r"], lines);
    }

    [Fact]
    public void Продолжение_последней_сессии_это_continue()
    {
        var lines = _builder.Build(Project(), new SessionLaunch.ContinueLast(), null);

        Assert.Equal(["claude --continue\r"], lines);
    }

    [Fact]
    public void Дополнительные_аргументы_не_подмешиваются_в_продолжение()
    {
        var project = Project(extraArgs: ["--model", "opus"]);

        Assert.Equal(["claude --continue\r"], _builder.Build(project, new SessionLaunch.ContinueLast(), null));
        Assert.Equal(["claude --resume s1\r"], _builder.Build(project, new SessionLaunch.ResumeSession("s1"), null));
    }

    [Fact]
    public void Команда_preLaunch_идёт_первой_строкой()
    {
        var lines = _builder.Build(Project(preLaunch: "git fetch --prune"), new SessionLaunch.NewSession(), null);

        Assert.Equal(["git fetch --prune\r", "claude\r"], lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Пустая_preLaunch_строки_не_добавляет(string? preLaunch)
    {
        var lines = _builder.Build(Project(preLaunch: preLaunch), new SessionLaunch.NewSession(), null);

        Assert.Equal(["claude\r"], lines);
    }

    [Theory]
    // Пробел, кавычки, $ и обратная кавычка в аргументе не должны разъехаться в PowerShell.
    [InlineData(@"D:\src\моё приложение", @"'D:\src\моё приложение'")]
    [InlineData("$env:PATH", "'$env:PATH'")]
    [InlineData("a`b", "'a`b'")]
    [InlineData("\"кавычки\"", "'\"кавычки\"'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a;b", "'a;b'")]
    [InlineData("a|b", "'a|b'")]
    [InlineData("a&b", "'a&b'")]
    [InlineData("--model", "--model")]
    [InlineData("opus-4.5", "opus-4.5")]
    public void Аргументы_экранируются_под_PowerShell(string argument, string expected)
    {
        var lines = _builder.Build(Project(extraArgs: [argument]), new SessionLaunch.NewSession(), null);

        Assert.Equal(["claude " + expected + "\r"], lines);
    }

    [Fact]
    public void Идентификатор_сессии_с_пробелом_тоже_экранируется()
    {
        var lines = _builder.Build(Project(), new SessionLaunch.ResumeSession("сессия с пробелом"), null);

        Assert.Equal(["claude --resume 'сессия с пробелом'\r"], lines);
    }

    [Fact]
    public void Путь_с_пробелом_остаётся_одним_аргументом()
    {
        var lines = _builder.Build(Project(extraArgs: ["--add-dir", @"D:\src\a b"]), new SessionLaunch.NewSession(), null);

        Assert.Equal([@"claude --add-dir 'D:\src\a b'" + "\r"], lines);
    }

    [Fact]
    public void Файл_настроек_с_хуками_уходит_в_settings()
    {
        var lines = _builder.Build(
            Project(),
            new SessionLaunch.NewSession(),
            @"C:\Users\ivan\AppData\Roaming\ClaudeAgentsShell\hook-settings.json");

        Assert.Equal(
            [@"claude --settings C:\Users\ivan\AppData\Roaming\ClaudeAgentsShell\hook-settings.json" + "\r"],
            lines);
    }

    [Fact]
    public void Путь_к_настройкам_с_пробелом_не_разъезжается()
    {
        // Каталог данных лежит в профиле пользователя, так что пробел в пути — обычное дело.
        var lines = _builder.Build(
            Project(),
            new SessionLaunch.NewSession(),
            @"C:\Users\Имя Фамилия\AppData\Roaming\ClaudeAgentsShell\hook-settings.json");

        Assert.Equal(
            [@"claude --settings 'C:\Users\Имя Фамилия\AppData\Roaming\ClaudeAgentsShell\hook-settings.json'" + "\r"],
            lines);
    }

    [Fact]
    public void Настройки_идут_и_в_resume_и_в_continue()
    {
        const string settings = @"C:\data\hook-settings.json";

        Assert.Equal(
            [@"claude --settings C:\data\hook-settings.json --resume s1" + "\r"],
            _builder.Build(Project(), new SessionLaunch.ResumeSession("s1"), settings));

        Assert.Equal(
            [@"claude --settings C:\data\hook-settings.json --continue" + "\r"],
            _builder.Build(Project(), new SessionLaunch.ContinueLast(), settings));
    }

    [Fact]
    public void Настройки_стоят_перед_дополнительными_аргументами_проекта()
    {
        var lines = _builder.Build(
            Project(extraArgs: ["--model", "opus"]),
            new SessionLaunch.NewSession(),
            @"C:\data\hook-settings.json");

        Assert.Equal([@"claude --settings C:\data\hook-settings.json --model opus" + "\r"], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Без_файла_настроек_пустого_settings_не_появляется(string? settings)
    {
        // null — это штатный запуск без хуков: вкладка живёт без маркера состояния.
        var lines = _builder.Build(Project(), new SessionLaunch.NewSession(), settings);

        Assert.Equal(["claude\r"], lines);
        Assert.DoesNotContain("--settings", lines[0], StringComparison.Ordinal);
    }

    private static ProjectDefinition Project(string? preLaunch = null, IReadOnlyList<string>? extraArgs = null) =>
        new(Guid.NewGuid(), "проект", @"D:\src\проект", ShellKind.Pwsh, preLaunch, extraArgs ?? [], 0);
}
