using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Наличие файлов задаётся тестом; диск не трогается.</summary>
internal sealed class FakeFileProbe : IFileProbe
{
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пути, о которых спрашивали, по порядку.</summary>
    public List<string> Requested { get; } = [];

    public void Add(string path) => _existing.Add(path);

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        Requested.Add(path);
        return Task.FromResult(_existing.Contains(path));
    }
}

/// <summary>Диалог настроек проекта (раздел 6.5 ТЗ) без окна.</summary>
public sealed class ProjectSettingsViewModelTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FakeFolderPicker _folderPicker = new();
    private readonly FakeDirectoryProbe _directoryProbe = new();
    private readonly FakeFileProbe _fileProbe = new();

    [Fact]
    public void Constructor_FillsFieldsFromProject()
    {
        var viewModel = Create(Project(preLaunch: "nvm use 20", extraArgs: ["--add-dir", @"C:\my repo"]));

        Assert.Equal(@"C:\repo", viewModel.ProjectPath);
        Assert.Equal("repo", viewModel.Name);
        Assert.Equal(ShellKind.Pwsh, viewModel.Shell.Kind);
        Assert.Equal("nvm use 20", viewModel.PreLaunch);
        Assert.Equal("--add-dir \"C:\\my repo\"", viewModel.ExtraArgsText);
    }

    [Fact]
    public void Constructor_NullPreLaunch_BecomesEmptyText()
    {
        var viewModel = Create(Project(preLaunch: null));

        Assert.Equal(string.Empty, viewModel.PreLaunch);
        Assert.Equal(string.Empty, viewModel.ExtraArgsText);
    }

    [Fact]
    public void Shell_PreselectedByValue()
    {
        var viewModel = Create(Project(shell: ShellKind.Cmd));

        // Список строится отдельно от выбранного значения: равенство обязано быть по значению,
        // иначе выпадающий список открылся бы пустым.
        Assert.Contains(viewModel.Shell, viewModel.Shells);
    }

    [Theory]
    [InlineData("", @"C:\repo", false)]
    [InlineData("   ", @"C:\repo", false)]
    [InlineData("repo", "", false)]
    [InlineData("repo", "   ", false)]
    [InlineData("repo", @"C:\repo", true)]
    public void IsValid_RequiresNameAndPath(string name, string path, bool expected)
    {
        var viewModel = Create(Project());

        viewModel.Name = name;
        viewModel.ProjectPath = path;

        Assert.Equal(expected, viewModel.IsValid);
    }

    [Fact]
    public void ValidationMessage_NamesWhatIsMissing()
    {
        var viewModel = Create(Project());

        viewModel.ProjectPath = string.Empty;
        Assert.Contains("каталог", viewModel.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.ProjectPath = @"C:\repo";
        viewModel.Name = string.Empty;
        Assert.Contains("имя", viewModel.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.Name = "repo";
        Assert.Equal(string.Empty, viewModel.ValidationMessage);
    }

    [Fact]
    public void Save_WhenInvalid_ProducesNothing()
    {
        var viewModel = Create(Project());
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        viewModel.Name = "   ";
        viewModel.Save();

        Assert.Null(viewModel.Result);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void Save_KeepsIdentityAndOrder()
    {
        var viewModel = Create(Project(order: 7));

        viewModel.Name = "Другое имя";
        viewModel.Save();

        var result = Assert.IsType<ProjectDefinition>(viewModel.Result);
        Assert.Equal(ProjectId, result.Id);
        Assert.Equal(7, result.Order);
    }

    [Fact]
    public void Save_TrimsFieldsAndParsesArguments()
    {
        var viewModel = Create(Project());
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        viewModel.Name = "  Мой проект  ";
        viewModel.ProjectPath = "  D:\\work\\repo  ";
        viewModel.Shell = ShellOptions.For(ShellKind.Cmd);
        viewModel.PreLaunch = "  nvm use 20  ";
        viewModel.ExtraArgsText = "  --add-dir \"D:\\my repo\"   --verbose ";
        viewModel.Save();

        var result = Assert.IsType<ProjectDefinition>(viewModel.Result);
        Assert.Equal("Мой проект", result.Name);
        Assert.Equal(@"D:\work\repo", result.Path);
        Assert.Equal(ShellKind.Cmd, result.Shell);
        Assert.Equal("nvm use 20", result.PreLaunch);
        Assert.Equal(["--add-dir", @"D:\my repo", "--verbose"], result.ExtraArgs);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Save_EmptyPreLaunch_BecomesNull()
    {
        var viewModel = Create(Project(preLaunch: "nvm use 20"));

        viewModel.PreLaunch = "   ";
        viewModel.Save();

        var result = Assert.IsType<ProjectDefinition>(viewModel.Result);
        Assert.Null(result.PreLaunch);
        Assert.Empty(result.ExtraArgs);
    }

    [Fact]
    public async Task RefreshAsync_MissingDirectory_WarnsButKeepsSavingAllowed()
    {
        var viewModel = Create(Project());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.True(viewModel.IsDirectoryMissing);
        Assert.NotEqual(string.Empty, viewModel.DirectoryWarning);

        // Раздел 8 ТЗ: исчезнувший каталог не должен мешать правке настроек.
        Assert.True(viewModel.IsValid);
    }

    [Fact]
    public async Task RefreshAsync_MissingDirectory_DoesNotProbeFiles()
    {
        var viewModel = Create(Project());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(_fileProbe.Requested);
        Assert.Contains("не проверял", viewModel.ClaudeMdHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_ReportsFoundAndMissingFiles()
    {
        _directoryProbe.Add(@"C:\repo");
        _fileProbe.Add(@"C:\repo\CLAUDE.md");

        var viewModel = Create(Project());
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(viewModel.IsDirectoryMissing);
        Assert.Equal(string.Empty, viewModel.DirectoryWarning);
        Assert.Equal("CLAUDE.md — найден", viewModel.ClaudeMdHint);
        Assert.Equal(".mcp.json — не найден", viewModel.McpJsonHint);
    }

    [Fact]
    public async Task ChangingPath_DropsStaleProbeResults()
    {
        _directoryProbe.Add(@"C:\repo");
        _fileProbe.Add(@"C:\repo\CLAUDE.md");

        var viewModel = Create(Project());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.Equal("CLAUDE.md — найден", viewModel.ClaudeMdHint);

        viewModel.ProjectPath = @"C:\other";

        // Про новый путь ещё ничего не известно — показывать старый ответ значило бы соврать.
        Assert.False(viewModel.IsDirectoryMissing);
        Assert.Contains("не проверял", viewModel.ClaudeMdHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_EmptyPath_ChecksNothing()
    {
        var viewModel = Create(Project());
        viewModel.ProjectPath = string.Empty;

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Empty(_fileProbe.Requested);
        Assert.False(viewModel.IsDirectoryMissing);
    }

    [Fact]
    public async Task BrowseAsync_Cancelled_ChangesNothing()
    {
        var viewModel = Create(Project());
        _folderPicker.NextFolder = null;

        await viewModel.BrowseAsync(CancellationToken.None);

        Assert.Equal(@"C:\repo", viewModel.ProjectPath);
        Assert.Equal("repo", viewModel.Name);
    }

    [Fact]
    public async Task BrowseAsync_KeepsNameTypedByUser()
    {
        var viewModel = Create(Project());
        viewModel.Name = "Моё имя";
        _folderPicker.NextFolder = @"D:\work\other";

        await viewModel.BrowseAsync(CancellationToken.None);

        Assert.Equal(@"D:\work\other", viewModel.ProjectPath);
        Assert.Equal("Моё имя", viewModel.Name);
    }

    [Fact]
    public async Task BrowseAsync_ReplacesNameThatWasTheOldFolderDefault()
    {
        // Имя «repo» совпадает с именем прежней папки — значит, его никто не правил.
        var viewModel = Create(Project());
        _folderPicker.NextFolder = @"D:\work\other";

        await viewModel.BrowseAsync(CancellationToken.None);

        Assert.Equal("other", viewModel.Name);
    }

    [Fact]
    public async Task BrowseAsync_FillsEmptyName()
    {
        var viewModel = Create(Project(name: string.Empty, path: string.Empty));
        _folderPicker.NextFolder = @"D:\work\other";

        await viewModel.BrowseAsync(CancellationToken.None);

        Assert.Equal("other", viewModel.Name);
        Assert.True(viewModel.IsValid);
    }

    [Fact]
    public async Task BrowseAsync_ChecksPickedDirectory()
    {
        _directoryProbe.Add(@"D:\work\other");
        _fileProbe.Add(@"D:\work\other\.mcp.json");

        var viewModel = Create(Project());
        _folderPicker.NextFolder = @"D:\work\other";

        await viewModel.BrowseAsync(CancellationToken.None);

        Assert.Equal(".mcp.json — найден", viewModel.McpJsonHint);
        Assert.Equal("CLAUDE.md — не найден", viewModel.ClaudeMdHint);
    }

    private static ProjectDefinition Project(
        string name = "repo",
        string path = @"C:\repo",
        ShellKind shell = ShellKind.Pwsh,
        string? preLaunch = null,
        IReadOnlyList<string>? extraArgs = null,
        int order = 0) =>
        new(ProjectId, name, path, shell, preLaunch, extraArgs ?? [], order);

    private ProjectSettingsViewModel Create(ProjectDefinition project) =>
        new(project, _folderPicker, _directoryProbe, _fileProbe);
}
