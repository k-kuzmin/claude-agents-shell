using ClaudeAgentsShell.Sessions.Launch;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class FileProbeTests
{
    private readonly FileProbe _probe = new();

    [Fact]
    public async Task Существующий_файл_находится()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("CLAUDE.md");
        await File.WriteAllTextAsync(file, "#", CancellationToken.None);

        Assert.True(await _probe.ExistsAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task Отсутствующий_файл_и_каталог_не_находятся()
    {
        using var temp = new TempDirectory();

        Assert.False(await _probe.ExistsAsync(temp.Combine(".mcp.json"), CancellationToken.None));

        // Каталог файлом не считается — для него есть отдельный порт.
        Assert.False(await _probe.ExistsAsync(temp.Path, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|негодный*путь?")]
    public async Task Пустой_и_негодный_путь_не_бросают(string path)
    {
        Assert.False(await _probe.ExistsAsync(path, CancellationToken.None));
    }
}

public sealed class ShellLauncherTests
{
    private readonly ShellLauncher _launcher = new();

    // Успешное открытие не проверяется: тест не должен поднимать проводник на машине разработчика.
    // Проверяется то, что важно для контракта — отказ вместо исключения.

    [Fact]
    public async Task Исчезнувший_каталог_даёт_отказ_а_не_исключение()
    {
        using var temp = new TempDirectory();

        Assert.False(await _launcher.OpenFolderAsync(temp.Combine("нет-такого"), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|негодный*путь?")]
    public async Task Пустой_и_негодный_путь_дают_отказ(string path)
    {
        Assert.False(await _launcher.OpenFolderAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Файл_вместо_каталога_даёт_отказ()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("projects.json");
        await File.WriteAllTextAsync(file, "{}", CancellationToken.None);

        Assert.False(await _launcher.OpenFolderAsync(file, CancellationToken.None));
    }
}
