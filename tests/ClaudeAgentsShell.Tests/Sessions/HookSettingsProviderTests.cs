using System.Text.Json;
using ClaudeAgentsShell.Sessions.Hooks;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class HookSettingsProviderTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:52341/hook");

    [Fact]
    public async Task Файл_настроек_создаётся_в_каталоге_данных_приложения()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var provider = CreateProvider(temp, appData, out var claudeProjects);

        var path = await provider.EnsureSettingsFileAsync(Endpoint, CancellationToken.None);

        Assert.Equal(Path.Combine(appData, "hook-settings.json"), path);
        Assert.True(File.Exists(path));

        // В проект пользователя и в ~/.claude не пишется ничего (раздел 7 CLAUDE.md).
        Assert.False(Directory.Exists(claudeProjects));
        Assert.Equal([appData], Directory.GetDirectories(temp.Path));
    }

    [Fact]
    public async Task Зарегистрированы_все_семь_хуков_и_каждый_зовёт_командный_файл()
    {
        using var temp = new TempDirectory();
        var provider = CreateProvider(temp, temp.Combine("appdata"), out _);

        var path = await provider.EnsureSettingsFileAsync(Endpoint, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, CancellationToken.None));
        var hooks = document.RootElement.GetProperty("hooks");

        // Семь зарегистрированных хуков — единственный источник состояния вкладки
        // (раздел 7 CLAUDE.md), но не весь набор хуков Claude Code: Notification сознательно
        // не регистрируется. Список точный и в обе стороны: лишнее имя Claude Code молча
        // пропустит, а недостающее оставит вкладку без перехода.
        string[] expected =
        [
            "SessionStart",
            "UserPromptSubmit",
            "Stop",
            "StopFailure",
            "SubagentStart",
            "SubagentStop",
            "SessionEnd",
        ];

        Assert.Equal(expected.Length, hooks.EnumerateObject().Count());

        foreach (var name in expected)
        {
            var command = hooks.GetProperty(name)[0].GetProperty("hooks")[0];

            Assert.Equal("command", command.GetProperty("type").GetString());
            Assert.Equal(
                $"\"{Path.Combine(temp.Combine("appdata"), "hook-send.cmd")}\"",
                command.GetProperty("command").GetString());
        }
    }

    [Fact]
    public async Task Командный_файл_шлёт_нагрузку_с_токеном_вкладки_и_молчит()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var provider = CreateProvider(temp, appData, out _);

        await provider.EnsureSettingsFileAsync(Endpoint, CancellationToken.None);
        var script = await File.ReadAllTextAsync(Path.Combine(appData, "hook-send.cmd"), CancellationToken.None);

        // Токен приходит из окружения псевдоконсоли — отдельного файла на сессию нет.
        Assert.Contains($"%{provider.TokenVariableName}%", script, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:52341/hook", script, StringComparison.Ordinal);

        // Нагрузка читается со stdin, вывода нет, код возврата всегда нулевой.
        Assert.Contains("--data-binary @-", script, StringComparison.Ordinal);
        Assert.Contains("@echo off", script, StringComparison.Ordinal);
        Assert.Contains("-o nul", script, StringComparison.Ordinal);
        Assert.Contains("exit /b 0", script, StringComparison.Ordinal);

        // Прокси из окружения пользователя не должен перехватывать вызов к самому себе.
        Assert.Contains("--noproxy", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Новый_порт_переписывает_оба_файла_и_мусора_не_оставляет()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var provider = CreateProvider(temp, appData, out _);

        await provider.EnsureSettingsFileAsync(Endpoint, CancellationToken.None);
        await provider.EnsureSettingsFileAsync(new Uri("http://127.0.0.1:60123/hook"), CancellationToken.None);

        var script = await File.ReadAllTextAsync(Path.Combine(appData, "hook-send.cmd"), CancellationToken.None);

        Assert.Contains("http://127.0.0.1:60123/hook", script, StringComparison.Ordinal);
        Assert.DoesNotContain("52341", script, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(appData, "*.tmp"));
    }

    [Fact]
    public void Имя_переменной_с_токеном_не_пустое()
    {
        using var temp = new TempDirectory();
        var provider = CreateProvider(temp, temp.Combine("appdata"), out _);

        Assert.False(string.IsNullOrWhiteSpace(provider.TokenVariableName));
    }

    private static HookSettingsProvider CreateProvider(TempDirectory temp, string appData, out string claudeProjects)
    {
        claudeProjects = temp.Combine("claude", "projects");
        return new HookSettingsProvider(new AppDataPaths(appData, claudeProjects));
    }
}
