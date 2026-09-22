using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class DiffCollapsePolicyTests
{
    private readonly DiffCollapsePolicy _policy = new(new GitDiffOptions());

    private static DiffFileEntry Entry(string path, int? added, int? deleted, DiffChangeKind kind = DiffChangeKind.Modified) =>
        new(path, null, kind, added, deleted, DiffCollapseReason.None);

    [Theory]
    [InlineData(400, 0, DiffCollapseReason.None)]
    [InlineData(200, 200, DiffCollapseReason.None)]
    [InlineData(201, 200, DiffCollapseReason.LargeDiff)]
    [InlineData(0, 401, DiffCollapseReason.LargeDiff)]
    public void Порог_строк_считает_добавленное_и_удалённое(int added, int deleted, DiffCollapseReason expected)
    {
        Assert.Equal(expected, _policy.Classify(Entry("src/a.cs", added, deleted), GitPathAttributes.None, requested: false));
    }

    [Fact]
    public void Порог_берётся_из_настроек()
    {
        var policy = new DiffCollapsePolicy(new GitDiffOptions { LargeDiffLineThreshold = 10 });

        Assert.Equal(DiffCollapseReason.LargeDiff, policy.Classify(Entry("a.cs", 11, 0), GitPathAttributes.None, requested: false));
    }

    [Fact]
    public void Бинарный_свёрнут_если_не_назван_агентом()
    {
        Assert.Equal(DiffCollapseReason.Binary, _policy.Classify(Entry("a.png", null, null), GitPathAttributes.None, requested: false));
        Assert.Equal(DiffCollapseReason.None, _policy.Classify(Entry("a.png", null, null), GitPathAttributes.None, requested: true));
        Assert.Equal(DiffCollapseReason.Binary, _policy.Classify(Entry("a.png", null, null, DiffChangeKind.Untracked), GitPathAttributes.None, requested: false));
    }

    [Fact]
    public void Новый_файл_сверх_потолка_подсчёта_большой()
    {
        Assert.Equal(DiffCollapseReason.LargeDiff, _policy.Classify(Entry("scene.unity", null, 0, DiffChangeKind.Untracked), GitPathAttributes.None, requested: false));
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("Cargo.lock")]
    [InlineData("dist/app.min.js")]
    [InlineData("style.MIN.css")]
    [InlineData("dist/app.js.map")]
    public void Встроенный_список_сгенерированных(string path)
    {
        Assert.True(DiffCollapsePolicy.IsBuiltInGenerated(path));
        Assert.Equal(DiffCollapseReason.Generated, _policy.Classify(Entry(path, 1, 0), GitPathAttributes.None, requested: false));
    }

    [Theory]
    [InlineData("src/a.cs")]
    [InlineData("lock")]
    [InlineData(".lock")]
    [InlineData("admin.cs")]
    [InlineData("minimal.txt")]
    [InlineData(".min.js")]
    public void Не_из_встроенного_списка(string path)
    {
        Assert.False(DiffCollapsePolicy.IsBuiltInGenerated(path));
    }

    [Theory]
    [InlineData("set", null, true)]
    [InlineData("true", null, true)]
    [InlineData(null, "unset", true)]
    [InlineData(null, null, false)]
    public void Атрибуты_gitattributes(string? linguist, string? diff, bool generated)
    {
        Assert.Equal(generated, DiffCollapsePolicy.IsGenerated("src/a.cs", new GitPathAttributes(linguist, diff)));
    }

    [Fact]
    public void Явный_linguist_generated_false_снимает_встроенный_список()
    {
        Assert.False(DiffCollapsePolicy.IsGenerated("yarn.lock", new GitPathAttributes("false", null)));
        Assert.False(DiffCollapsePolicy.IsGenerated("yarn.lock", new GitPathAttributes("unset", null)));
    }

    [Fact]
    public void Сгенерированный_важнее_большого()
    {
        Assert.Equal(DiffCollapseReason.Generated, _policy.Classify(Entry("package-lock.json", 5000, 3000), GitPathAttributes.None, requested: false));
    }

    [Fact]
    public void Названный_агентом_не_сворачивается()
    {
        Assert.Equal(DiffCollapseReason.None, _policy.Classify(Entry("package-lock.json", 5000, 3000), GitPathAttributes.None, requested: true));
        Assert.Equal(DiffCollapseReason.None, _policy.Classify(Entry("gen.cs", 1, 0), new GitPathAttributes("set", null), requested: true));
    }
}
