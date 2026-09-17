using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Каталог_данных_создаётся_при_первом_обращении()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var paths = new AppDataPaths(appData, temp.Combine("claude"));

        Assert.False(Directory.Exists(appData));

        var resolved = paths.AppData;

        Assert.True(Directory.Exists(resolved));
    }

    [Fact]
    public void Файл_проектов_лежит_в_созданном_каталоге_данных()
    {
        using var temp = new TempDirectory();
        var appData = temp.Combine("appdata");
        var paths = new AppDataPaths(appData, temp.Combine("claude"));

        var file = paths.ProjectsFile;

        Assert.Equal(Path.Combine(appData, "projects.json"), file);
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void Каталог_Claude_Code_не_создаётся_чтением_пути()
    {
        using var temp = new TempDirectory();
        var claude = temp.Combine("claude", "projects");
        var paths = new AppDataPaths(temp.Combine("appdata"), claude);

        var resolved = paths.ClaudeProjects;

        Assert.Equal(claude, resolved);
        Assert.False(Directory.Exists(claude));
    }
}
