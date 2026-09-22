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
    public async Task Зарегистрирован_точный_набор_хуков_и_PreToolUse_среди_них_нет()
    {
        using var temp = new TempDirectory();
        var provider = CreateProvider(temp, temp.Combine("appdata"), out _);

        var hooks = await ReadHooks(provider);

        // Список точный и в обе стороны: лишнее имя Claude Code молча пропустит, а недостающее
        // оставит вкладку без перехода. PreToolUse при недоступном HTTP-приёмнике отказывает
        // инструменту, поэтому его здесь быть не должно.
        string[] expected =
        [
            "PermissionRequest",
            "PostToolBatch",
            "SessionEnd",
            "SessionStart",
            "Stop",
            "StopFailure",
            "SubagentStart",
            "SubagentStop",
            "UserPromptSubmit",
        ];

        Assert.Equal(expected, hooks.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));

        foreach (var name in expected)
        {
            var matchers = hooks.GetProperty(name);
            Assert.Equal(1, matchers.GetArrayLength());

            // Без matcher — хук срабатывает на любой инструмент и любой source.
            Assert.False(matchers[0].TryGetProperty("matcher", out _));
            Assert.Equal(1, matchers[0].GetProperty("hooks").GetArrayLength());
        }
    }

    [Fact]
    public async Task SessionStart_остаётся_командным_хуком_на_командный_файл()
    {
        using var temp = new TempDirectory();
        var provider = CreateProvider(temp, temp.Combine("appdata"), out _);

        var hooks = await ReadHooks(provider);
        var handler = hooks.GetProperty("SessionStart")[0].GetProperty("hooks")[0];

        // HTTP-хуки SessionStart не поддерживает.
        Assert.Equal(["command", "timeout", "type"], PropertyNames(handler));
        Assert.Equal("command", handler.GetProperty("type").GetString());
        Assert.Equal(
            $"\"{Path.Combine(temp.Combine("appdata"), "hook-send.cmd")}\"",
            handler.GetProperty("command").GetString());
        Assert.Equal(5, handler.GetProperty("timeout").GetInt32());
    }

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("Stop")]
    [InlineData("StopFailure")]
    [InlineData("SubagentStart")]
    [InlineData("SubagentStop")]
    [InlineData("SessionEnd")]
    [InlineData("PostToolBatch")]
    [InlineData("PermissionRequest")]
    public async Task Остальные_хуки_HTTP_с_токеном_вкладки_в_заголовке(string name)
    {
        using var temp = new TempDirectory();
        var provider = CreateProvider(temp, temp.Combine("appdata"), out _);

        var hooks = await ReadHooks(provider);
        var handler = hooks.GetProperty(name)[0].GetProperty("hooks")[0];

        // Ровно поля HTTP-хука: пустой command рядом с url мог бы сделать файл невалидным.
        Assert.Equal(["allowedEnvVars", "headers", "timeout", "type", "url"], PropertyNames(handler));
        Assert.Equal("http", handler.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:52341/hook", handler.GetProperty("url").GetString());
        Assert.Equal(3, handler.GetProperty("timeout").GetInt32());

        // Токен у каждой вкладки свой, файл общий: в заголовке ссылка на переменную окружения,
        // и подставить её Claude Code разрешено только через allowedEnvVars.
        var headers = handler.GetProperty("headers");
        Assert.Equal(["X-Agents-Shell-Token"], PropertyNames(headers));
        Assert.Equal("$" + provider.TokenVariableName, headers.GetProperty("X-Agents-Shell-Token").GetString());
        Assert.Equal(
            [provider.TokenVariableName],
            handler.GetProperty("allowedEnvVars").EnumerateArray().Select(static v => v.GetString()));
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

    private static async Task<JsonElement> ReadHooks(HookSettingsProvider provider)
    {
        var path = await provider.EnsureSettingsFileAsync(Endpoint, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, CancellationToken.None));
        return document.RootElement.GetProperty("hooks").Clone();
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal)];

    private static HookSettingsProvider CreateProvider(TempDirectory temp, string appData, out string claudeProjects)
    {
        claudeProjects = temp.Combine("claude", "projects");
        return new HookSettingsProvider(new AppDataPaths(appData, claudeProjects));
    }
}
