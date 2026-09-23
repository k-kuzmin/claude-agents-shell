using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.Domain;

/// <summary>Пути агента приводятся к форме путей оглавления diff (issue #5).</summary>
public sealed class DiffPathsTests
{
    private const string Root = @"D:\repo";

    [Theory]
    [InlineData(@"src\a.cs", "src/a.cs")]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("/src/a.cs", "src/a.cs")]
    [InlineData("src//a.cs", "src/a.cs")]
    [InlineData(@"D:\repo\папка\файл с пробелом.cs", "папка/файл с пробелом.cs")]
    [InlineData("D:/repo/a.cs", "a.cs")]
    [InlineData(@"d:\repo\a.cs", "a.cs")]
    [InlineData("src/../a.cs", "a.cs")]
    [InlineData("src/./b/../a.cs", "src/a.cs")]
    [InlineData(@"D:\repo\..cache\x", "..cache/x")]
    [InlineData("..cache/x", "..cache/x")]
    [InlineData("src/..a/b.cs", "src/..a/b.cs")]
    [InlineData("SRC/B.cs", "SRC/B.cs")]
    public void Путь_внутри_корня_приводится_к_pathspec(string input, string expected)
    {
        Assert.Equal(expected, DiffPaths.NormalizeRequested(input, Root));
    }

    [Theory]
    [InlineData(@"D:\other\a.cs")]
    [InlineData(@"E:\repo\a.cs")]
    [InlineData(@"D:\repo")]
    [InlineData(@"D:\repo\")]
    [InlineData("../x.cs")]
    [InlineData(@"..\x.cs")]
    [InlineData("a/../../x.cs")]
    [InlineData("src/..")]
    [InlineData(".")]
    [InlineData("  ")]
    public void Путь_вне_корня_или_пустой_отбрасывается(string input)
    {
        Assert.Null(DiffPaths.NormalizeRequested(input, Root));
    }

    [Fact]
    public void Список_схлопывает_повторы_сохраняет_порядок_и_отбрасывает_чужие()
    {
        var result = DiffPaths.NormalizeRequested(
            [@"b\c.cs", "a.cs", "./b/c.cs", "../x.cs", @"D:\repo\a.cs", " ", "src/../d.cs"],
            Root);

        Assert.Equal(["b/c.cs", "a.cs", "d.cs"], result);
    }
}
