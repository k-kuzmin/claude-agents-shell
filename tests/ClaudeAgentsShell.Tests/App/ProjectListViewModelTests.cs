using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Панель проектов: добавление через диалог настроек (раздел 6.5 ТЗ). Проверяется сама
/// ViewModel списка — вкладки и главное окно к добавлению отношения не имеют.
/// </summary>
public sealed class ProjectListViewModelTests
{
    private const string GammaPath = @"D:\src\gamma";

    private readonly FakeProjectStore _store = new();
    private readonly FakeGitBranchReader _branchReader = new();
    private readonly FakeGitBranchWatcher _watcher = new();
    private readonly FakeDirectoryProbe _probe = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly FakeProjectSettingsDialog _dialog = new();
    private readonly FakeShellLauncher _launcher = new();
    private readonly FakeUserPrompt _prompt = new();

    [Fact]
    public async Task AddProject_StoresEverythingTheDialogReturned()
    {
        var list = Create();
        _picker.NextFolder = GammaPath;
        _probe.Add(GammaPath);
        _dialog.Edit = project => project with
        {
            Name = "Гамма",
            Shell = ShellKind.Cmd,
            PreLaunch = "nvm use 20",
            ExtraArgs = ["--add-dir", @"D:\my repo"],
        };

        var row = await list.AddProjectAsync(CancellationToken.None);

        Assert.NotNull(row);
        Assert.Same(row, Assert.Single(list.Rows));
        Assert.Equal("Гамма", row!.Name);

        var saved = Assert.Single(_store.Saved);
        Assert.Equal("Гамма", saved.Name);
        Assert.Equal(GammaPath, saved.Path);
        Assert.Equal(ShellKind.Cmd, saved.Shell);
        Assert.Equal("nvm use 20", saved.PreLaunch);
        Assert.Equal(["--add-dir", @"D:\my repo"], saved.ExtraArgs);
        Assert.Equal(0, saved.Order);
    }

    [Fact]
    public async Task AddProject_ShowsTheDialogWithFolderDefaults()
    {
        var list = Create();
        _picker.NextFolder = GammaPath;
        _dialog.Edit = project => project;

        await list.AddProjectAsync(CancellationToken.None);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal("gamma", shown.Name);
        Assert.Equal(GammaPath, shown.Path);
        Assert.Equal(ShellKind.Pwsh, shown.Shell);
        Assert.Null(shown.PreLaunch);
        Assert.Empty(shown.ExtraArgs);
        Assert.Equal(ProjectSettingsPurpose.Add, Assert.Single(_dialog.Purposes));
    }

    [Fact]
    public async Task AddProject_CancelledFolder_ShowsNoDialogAndAddsNothing()
    {
        var list = Create();
        _picker.NextFolder = null;

        Assert.Null(await list.AddProjectAsync(CancellationToken.None));

        Assert.Empty(_dialog.Shown);
        Assert.Empty(list.Rows);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task AddProject_CancelledDialog_AddsNothing()
    {
        var list = Create();
        _picker.NextFolder = GammaPath;

        // Edit не задан — пользователь закрыл диалог без сохранения.
        Assert.Null(await list.AddProjectAsync(CancellationToken.None));

        Assert.Single(_dialog.Shown);
        Assert.Empty(list.Rows);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task AddProject_FailedSave_LeavesNoRowInMemory()
    {
        var list = Create();
        _picker.NextFolder = GammaPath;
        _dialog.Edit = project => project;
        _store.SaveFailure = new IOException("диск занят");

        await Assert.ThrowsAsync<IOException>(() => list.AddProjectAsync(CancellationToken.None));

        Assert.Empty(list.Rows);
    }

    [Fact]
    public async Task AddProject_PathChangedInTheDialogToATwin_AddsNothing()
    {
        _store.Seed(new ProjectDefinition(Guid.NewGuid(), "alpha", GammaPath, ShellKind.Pwsh, null, [], 0));
        _probe.Add(GammaPath);

        var list = Create();
        await list.LoadAsync(CancellationToken.None);

        _picker.NextFolder = @"D:\src\delta";
        _dialog.Edit = project => project with { Path = GammaPath + @"\" };

        var row = await list.AddProjectAsync(CancellationToken.None);

        Assert.Same(list.Rows[0], row);
        Assert.Single(list.Rows);
        Assert.Equal(0, _store.SaveCount);

        // Молча выброшенные настройки выглядят как не сработавшая кнопка: сообщение
        // обязано назвать проект, который уже занимает каталог.
        var message = Assert.Single(_prompt.Errors);
        Assert.Contains("alpha", message, StringComparison.Ordinal);
        Assert.Contains(GammaPath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProject_FolderAlreadyInTheList_ShowsNoDialogAndSaysNothing()
    {
        _store.Seed(new ProjectDefinition(Guid.NewGuid(), "alpha", GammaPath, ShellKind.Pwsh, null, [], 0));
        _probe.Add(GammaPath);

        var list = Create();
        await list.LoadAsync(CancellationToken.None);

        _picker.NextFolder = GammaPath;

        Assert.Same(list.Rows[0], await list.AddProjectAsync(CancellationToken.None));

        // Здесь пользователь ещё ничего не вводил: показанная строка сама по себе говорит,
        // что каталог уже в списке, и модальное окно поверх неё было бы шумом.
        Assert.Empty(_dialog.Shown);
        Assert.Empty(_prompt.Errors);
    }

    [Fact]
    public async Task AddProject_SecondProject_GetsTheNextOrder()
    {
        var list = Create();
        _dialog.Edit = project => project;

        _picker.NextFolder = GammaPath;
        await list.AddProjectAsync(CancellationToken.None);
        _picker.NextFolder = @"D:\src\delta";
        await list.AddProjectAsync(CancellationToken.None);

        Assert.Equal([0, 1], _store.Saved.Select(project => project.Order));
        Assert.Equal(2, list.Rows.Count);
    }

    [Fact]
    public async Task EditProject_ShowsTheDialogAsEdit()
    {
        _store.Seed(new ProjectDefinition(Guid.NewGuid(), "alpha", GammaPath, ShellKind.Pwsh, null, [], 0));
        _probe.Add(GammaPath);

        var list = Create();
        await list.LoadAsync(CancellationToken.None);
        _dialog.Edit = project => project with { Name = "Альфа" };

        Assert.True(await list.EditProjectAsync(list.Rows[0], CancellationToken.None));

        Assert.Equal(ProjectSettingsPurpose.Edit, Assert.Single(_dialog.Purposes));
        Assert.Equal("Альфа", list.Rows[0].Name);
    }

    private ProjectListViewModel Create() =>
        new(_store, _branchReader, _watcher, _probe, _picker, _dialog, _prompt, _launcher, new InlineUiDispatcher());
}
