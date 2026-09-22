using System.Text.Json;
using ClaudeAgentsShell.Sessions.Mcp;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// <c>mcp.json</c> для <c>--mcp-config</c> (issue #5). Токен — ссылка <c>${ПЕРЕМЕННАЯ}</c>:
/// подстановку в заголовках файла <c>--mcp-config</c> проверили вживую на claude 2.1.280.
/// </summary>
public sealed class McpConfigProviderTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:52341/mcp");

    [Fact]
    public async Task Файл_создаётся_в_каталоге_данных_приложения()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var provider = new McpConfigProvider(new AppDataPaths(appData, temp.Combine("claude", "projects")));

        var path = await provider.EnsureConfigFileAsync(Endpoint, CancellationToken.None);

        Assert.Equal(Path.Combine(appData, "mcp.json"), path);
        Assert.True(File.Exists(path));

        // Ни временного файла, ни следов в каталоге Claude Code.
        Assert.Equal([path], Directory.GetFiles(appData));
        Assert.Equal([appData], Directory.GetDirectories(temp.Path));
    }

    [Fact]
    public async Task Один_HTTP_сервер_agents_shell_с_токеном_из_переменной()
    {
        using var temp = new TempDirectory();
        var provider = new McpConfigProvider(new AppDataPaths(temp.Combine("appdata"), temp.Combine("projects")));

        var path = await provider.EnsureConfigFileAsync(Endpoint, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var servers = document.RootElement.GetProperty("mcpServers");
        var server = Assert.Single(servers.EnumerateObject());
        Assert.Equal("agents-shell", server.Name);

        Assert.Equal("http", server.Value.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:52341/mcp", server.Value.GetProperty("url").GetString());

        var header = Assert.Single(server.Value.GetProperty("headers").EnumerateObject());
        Assert.Equal("X-Agents-Shell-Token", header.Name);

        // Синтаксис mcp.json — фигурные скобки; $ИМЯ из HTTP-хуков здесь не подставился бы.
        Assert.Equal("${CLAUDE_AGENTS_SHELL_TOKEN}", header.Value.GetString());
    }

    [Fact]
    public async Task Повторный_вызов_переписывает_файл_под_новый_порт()
    {
        using var temp = new TempDirectory();
        var provider = new McpConfigProvider(new AppDataPaths(temp.Combine("appdata"), temp.Combine("projects")));

        await provider.EnsureConfigFileAsync(Endpoint, CancellationToken.None);
        var path = await provider.EnsureConfigFileAsync(new Uri("http://127.0.0.1:60001/mcp"), CancellationToken.None);

        Assert.Contains("http://127.0.0.1:60001/mcp", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }
}
