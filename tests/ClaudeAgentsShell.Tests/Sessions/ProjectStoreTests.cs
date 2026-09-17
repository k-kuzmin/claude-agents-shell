using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class ProjectStoreTests
{
    private static ProjectDefinition Project(string name, int order = 0, ShellKind shell = ShellKind.Pwsh) =>
        new(Guid.NewGuid(), name, @"D:\src\" + name, shell, null, [], order);

    [Fact]
    public async Task Первая_запись_создаёт_файл_которого_ещё_нет()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var project = Project("домовой");

        await store.SaveAsync([project], CancellationToken.None);

        Assert.True(File.Exists(paths.ProjectsFile));
        var loaded = await store.LoadAsync(CancellationToken.None);
        AssertSameProject(project, Assert.Single(loaded));
    }

    [Fact]
    public async Task Повторная_запись_заменяет_файл_целиком()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out _);

        await store.SaveAsync([Project("первый")], CancellationToken.None);
        var second = Project("второй", order: 3, shell: ShellKind.Cmd);
        await store.SaveAsync([second], CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        AssertSameProject(second, Assert.Single(loaded));
    }

    [Fact]
    public async Task Сбой_записи_оставляет_прежний_файл_целым()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var original = Project("уцелевший");
        await store.SaveAsync([original], CancellationToken.None);
        var contentBefore = await File.ReadAllTextAsync(paths.ProjectsFile, CancellationToken.None);

        // Каталог на месте временного файла: запись обязана сорваться до замены.
        Directory.CreateDirectory(paths.ProjectsFile + ".tmp");

        var failure = await Record.ExceptionAsync(
            () => store.SaveAsync([Project("новый")], CancellationToken.None));

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            "ожидался сбой файловой операции, получено: " + (failure?.GetType().Name ?? "ничего"));

        Assert.Equal(contentBefore, await File.ReadAllTextAsync(paths.ProjectsFile, CancellationToken.None));
        var loaded = await store.LoadAsync(CancellationToken.None);
        AssertSameProject(original, Assert.Single(loaded));
    }

    [Fact]
    public async Task Отсутствующий_файл_читается_как_пустой_список()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out _);

        Assert.Empty(await store.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ это не json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"version\": 1, \"projects\": \"строка вместо списка\"}")]
    [InlineData("{\"version\": 99, \"projects\": [{\"id\":\"9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f\",\"name\":\"n\",\"path\":\"p\"}]}")]
    public async Task Нечитаемое_содержимое_читается_как_пустой_список(string content)
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        await File.WriteAllTextAsync(paths.ProjectsFile, content, CancellationToken.None);

        Assert.Empty(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Необязательные_поля_и_лишние_ключи_не_мешают_чтению()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        await File.WriteAllTextAsync(
            paths.ProjectsFile,
            """
            {
              "version": 1,
              "нечто": true,
              "projects": [
                { "id": "9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f", "name": "Домовой", "path": "D:/src/domovoy", "shell": "cmd", "order": 2, "чужое": 1 }
              ]
            }
            """,
            CancellationToken.None);

        var project = Assert.Single(await store.LoadAsync(CancellationToken.None));

        Assert.Equal("Домовой", project.Name);
        Assert.Equal("D:/src/domovoy", project.Path);
        Assert.Equal(ShellKind.Cmd, project.Shell);
        Assert.Null(project.PreLaunch);
        Assert.Empty(project.ExtraArgs);
        Assert.Equal(2, project.Order);
    }

    [Fact]
    public async Task Битая_запись_пропускается_а_остальные_читаются()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        await File.WriteAllTextAsync(
            paths.ProjectsFile,
            """
            {
              "version": 1,
              "projects": [
                { "id": "не guid", "name": "битый", "path": "D:/src/x" },
                { "id": "9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f", "name": "целый", "path": "D:/src/y", "preLaunch": "git fetch --prune", "extraArgs": ["--model", "opus"] }
              ]
            }
            """,
            CancellationToken.None);

        var project = Assert.Single(await store.LoadAsync(CancellationToken.None));

        Assert.Equal("целый", project.Name);
        Assert.Equal("git fetch --prune", project.PreLaunch);
        Assert.Equal(["--model", "opus"], project.ExtraArgs);
    }

    [Fact]
    public async Task Формат_файла_соответствует_разделу_4_1()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var project = new ProjectDefinition(
            Guid.Parse("9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f"),
            "Домовой",
            @"D:\src\domovoy",
            ShellKind.WindowsPowerShell,
            "git fetch --prune",
            ["--model", "opus"],
            0);

        await store.SaveAsync([project], CancellationToken.None);

        var json = await File.ReadAllTextAsync(paths.ProjectsFile, CancellationToken.None);

        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"shell\": \"powershell\"", json, StringComparison.Ordinal);
        Assert.Contains("\"preLaunch\": \"git fetch --prune\"", json, StringComparison.Ordinal);
        Assert.Contains("\"extraArgs\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Записанное_читается_обратно_без_потерь()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out _);
        ProjectDefinition[] projects =
        [
            new(Guid.NewGuid(), "Домовой", @"D:\src\domovoy", ShellKind.Pwsh, "git fetch", ["--model", "opus"], 0),
            new(Guid.NewGuid(), "Shell", @"D:\src\shell", ShellKind.Cmd, null, [], 1),
        ];

        await store.SaveAsync(projects, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(projects.Length, loaded.Count);
        for (var i = 0; i < projects.Length; i++)
        {
            AssertSameProject(projects[i], loaded[i]);
        }
    }

    [Fact]
    public async Task Файл_чужой_версии_копируется_рядом_перед_перезаписью()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        const string foreign = """
            { "version": 2, "projects": [ { "id": "9f2c0f4e-0a1b-4c2d-8e3f-0a1b2c3d4e5f", "name": "из будущего", "path": "D:/src/future", "workspace": "чего мы ещё не знаем" } ] }
            """;
        await File.WriteAllTextAsync(paths.ProjectsFile, foreign, CancellationToken.None);

        await store.SaveAsync([Project("новый")], CancellationToken.None);

        var backup = paths.ProjectsFile + ".bak";
        Assert.True(File.Exists(backup), "копия файла чужой версии не создана");
        Assert.Equal(foreign, await File.ReadAllTextAsync(backup, CancellationToken.None));

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("новый", Assert.Single(loaded).Name);
    }

    [Fact]
    public async Task Битый_файл_тоже_копируется_а_свой_нет()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        await File.WriteAllTextAsync(paths.ProjectsFile, "{ это не json", CancellationToken.None);

        await store.SaveAsync([Project("первый")], CancellationToken.None);

        var backup = paths.ProjectsFile + ".bak";
        Assert.Equal("{ это не json", await File.ReadAllTextAsync(backup, CancellationToken.None));

        // Файл своей версии копией не обрастает: перезапись собственных данных — штатная работа.
        await store.SaveAsync([Project("второй")], CancellationToken.None);

        Assert.Equal("{ это не json", await File.ReadAllTextAsync(backup, CancellationToken.None));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(paths.ProjectsFile)!, "*.bak"));
    }

    /// <summary>
    /// Сравнение по полям, а не через равенство записи: ProjectDefinition.ExtraArgs — это
    /// IReadOnlyList, и равенство записи сравнило бы списки по ссылке.
    /// </summary>
    private static void AssertSameProject(ProjectDefinition expected, ProjectDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Shell, actual.Shell);
        Assert.Equal(expected.PreLaunch, actual.PreLaunch);
        Assert.Equal(expected.ExtraArgs, actual.ExtraArgs);
        Assert.Equal(expected.Order, actual.Order);
    }

    private static ProjectStore CreateStore(TempDirectory temp, out AppDataPaths paths)
    {
        paths = new AppDataPaths(temp.Combine("appdata"), temp.Combine("claude"));
        return new ProjectStore(paths);
    }
}
