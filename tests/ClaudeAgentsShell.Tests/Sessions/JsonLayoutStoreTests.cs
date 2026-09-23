using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Storage;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class JsonLayoutStoreTests
{
    private static JsonLayoutStore CreateStore(TempDirectory temp, out AppDataPaths paths)
    {
        paths = new AppDataPaths(temp.Combine("appdata"), temp.Combine("claude", "projects"));
        return new JsonLayoutStore(paths);
    }

    private static WorkspaceLayout Sample(out Guid first, out Guid second)
    {
        first = Guid.NewGuid();
        second = Guid.NewGuid();
        return new WorkspaceLayout(
            second,
            [
                new ProjectLayout(first, 1, [new TabLayout("s-1", "первая"), new TabLayout(null, null), new TabLayout("s-3", "третья")]),
                new ProjectLayout(second, 0, [new TabLayout("s-4", "четвёртая")]),
            ]);
    }

    private static void AssertSameLayout(WorkspaceLayout expected, WorkspaceLayout actual)
    {
        Assert.Equal(expected.ActiveProjectId, actual.ActiveProjectId);
        Assert.Equal(expected.Projects.Count, actual.Projects.Count);
        for (var i = 0; i < expected.Projects.Count; i++)
        {
            Assert.Equal(expected.Projects[i].ProjectId, actual.Projects[i].ProjectId);
            Assert.Equal(expected.Projects[i].ActiveTabIndex, actual.Projects[i].ActiveTabIndex);
            Assert.Equal(expected.Projects[i].Tabs, actual.Projects[i].Tabs);
        }
    }

    [Fact]
    public async Task Круг_записи_и_чтения_сохраняет_раскладку_и_порядок_вкладок()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out _);
        var layout = Sample(out _, out _);

        await store.SaveAsync(layout, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        AssertSameLayout(layout, loaded);
    }

    [Fact]
    public async Task Повторная_запись_заменяет_файл_целиком()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out _);

        await store.SaveAsync(Sample(out _, out _), CancellationToken.None);
        var second = new WorkspaceLayout(null, [new ProjectLayout(Guid.NewGuid(), 0, [new TabLayout("x", null)])]);
        await store.SaveAsync(second, CancellationToken.None);

        AssertSameLayout(second, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Файл_записан_в_формате_из_issue()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);

        await store.SaveAsync(Sample(out _, out _), CancellationToken.None);
        var text = await File.ReadAllTextAsync(paths.LayoutFile);

        Assert.Equal(Path.Combine(paths.AppData, "layout.json"), paths.LayoutFile);
        Assert.Contains("\"version\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"activeProjectId\"", text, StringComparison.Ordinal);
        Assert.Contains("\"projectId\"", text, StringComparison.Ordinal);
        Assert.Contains("\"activeTabIndex\"", text, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\": null", text, StringComparison.Ordinal);
        Assert.Contains("\"title\"", text, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.LayoutFile + ".tmp"));
    }

    [Fact]
    public async Task Нет_файла_это_пустая_раскладка_без_копии()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Same(WorkspaceLayout.Empty, loaded);
        Assert.False(File.Exists(paths.LayoutFile + JsonLayoutStore.BackupSuffix));
    }

    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\": 2, \"projects\": []}")]
    [InlineData("{\"projects\": []}")]
    [InlineData("{\"version\": 1}")]
    public async Task Негодный_файл_даёт_пустую_раскладку_и_уходит_в_bak(string content)
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        await File.WriteAllTextAsync(paths.LayoutFile, content);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(loaded.Projects);
        Assert.Null(loaded.ActiveProjectId);
        var backup = paths.LayoutFile + JsonLayoutStore.BackupSuffix;
        Assert.True(File.Exists(backup));
        Assert.Equal(content, await File.ReadAllTextAsync(backup));
        Assert.False(File.Exists(paths.LayoutFile));
    }

    [Fact]
    public async Task Второй_негодный_файл_не_затирает_первую_копию()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var backup = paths.LayoutFile + JsonLayoutStore.BackupSuffix;

        await File.WriteAllTextAsync(paths.LayoutFile, "первый");
        await store.LoadAsync(CancellationToken.None);
        await File.WriteAllTextAsync(paths.LayoutFile, "второй");
        await store.LoadAsync(CancellationToken.None);

        Assert.Equal("первый", await File.ReadAllTextAsync(backup));
        var copies = Directory.GetFiles(paths.AppData, "*" + JsonLayoutStore.BackupSuffix);
        Assert.Equal(2, copies.Length);
    }

    [Fact]
    public async Task Битая_запись_внутри_годного_файла_пропускается_без_копии()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var good = Guid.NewGuid();
        await File.WriteAllTextAsync(paths.LayoutFile, $$"""
            {
              "version": 1,
              "activeProjectId": "не guid",
              "projects": [
                { "projectId": "мусор", "activeTabIndex": 0, "tabs": [ { "sessionId": "a", "title": null } ] },
                null,
                { "projectId": "{{good}}", "activeTabIndex": 2, "tabs": [ null, { "sessionId": "b" }, { "sessionId": " ", "title": "имя" } ] }
              ]
            }
            """);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Null(loaded.ActiveProjectId);
        var project = Assert.Single(loaded.Projects);
        Assert.Equal(good, project.ProjectId);
        Assert.Equal(new[] { new TabLayout("b", null), new TabLayout(null, "имя") }, project.Tabs);

        // Активной была третья запись; после пропуска битой первой она вторая.
        Assert.Equal(1, project.ActiveTabIndex);
        Assert.False(File.Exists(paths.LayoutFile + JsonLayoutStore.BackupSuffix));
    }

    [Fact]
    public async Task Метка_порядка_байтов_в_начале_файла_не_мешает_чтению()
    {
        using var temp = new TempDirectory();
        using var store = CreateStore(temp, out var paths);
        var id = Guid.NewGuid();
        var json = $$"""{"version":1,"activeProjectId":"{{id}}","projects":[{"projectId":"{{id}}","activeTabIndex":0,"tabs":[{"sessionId":"s","title":"t"}]}]}""";
        await File.WriteAllBytesAsync(paths.LayoutFile, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(json)]);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(id, loaded.ActiveProjectId);
        Assert.Equal(new[] { new TabLayout("s", "t") }, Assert.Single(loaded.Projects).Tabs);
    }
}
