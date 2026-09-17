using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class DirectoryProbeTests
{
    private readonly DirectoryProbe _probe = new();

    [Fact]
    public async Task Существующий_каталог_находится()
    {
        using var temp = new TempDirectory();

        Assert.True(await _probe.ExistsAsync(temp.Path, CancellationToken.None));
    }

    [Fact]
    public async Task Исчезнувший_каталог_не_находится()
    {
        using var temp = new TempDirectory();

        Assert.False(await _probe.ExistsAsync(temp.Combine("нет-такого"), CancellationToken.None));
    }

    [Fact]
    public async Task Файл_каталогом_не_считается()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("projects.json");
        await File.WriteAllTextAsync(file, "{}", CancellationToken.None);

        Assert.False(await _probe.ExistsAsync(file, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|недопустимый*путь?")]
    public async Task Пустой_и_негодный_путь_не_бросают(string path)
    {
        Assert.False(await _probe.ExistsAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Отменённый_токен_освобождает_вызывающего()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _probe.ExistsAsync(temp.Path, cancellation.Token));
    }
}
