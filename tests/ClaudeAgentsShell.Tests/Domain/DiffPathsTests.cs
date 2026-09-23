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
    [InlineData("../x.cs")]
    [InlineData(@"..\x.cs")]
    [InlineData("a/../../x.cs")]
    [InlineData("  ")]
    public void Путь_вне_корня_или_пустой_отбрасывается(string input)
    {
        Assert.Null(DiffPaths.NormalizeRequested(input, Root));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("src/..")]
    [InlineData(@"D:\repo")]
    [InlineData(@"D:\repo\")]
    [InlineData("/")]
    public void Корень_отличается_от_выхода_за_корень(string input)
    {
        Assert.Equal(DiffPaths.Root, DiffPaths.NormalizeRequested(input, Root));
    }

    [Fact]
    public void Корень_в_списке_снимает_сужение()
    {
        var result = DiffPaths.NormalizeRequested(["a.cs", ".", "../x.cs"], Root);

        Assert.Empty(result.Paths);
        Assert.False(result.AllOutside);
    }

    [Fact]
    public void Список_только_из_чужих_путей_помечается_как_вне_корня()
    {
        Assert.True(DiffPaths.NormalizeRequested(["../x.cs", @"E:\a.cs"], Root).AllOutside);
        Assert.False(DiffPaths.NormalizeRequested([" "], Root).AllOutside);
        Assert.False(DiffPaths.NormalizeRequested([], Root).AllOutside);
    }

    [Fact]
    public void Список_схлопывает_повторы_сохраняет_порядок_и_отбрасывает_чужие()
    {
        var result = DiffPaths.NormalizeRequested(
            [@"b\c.cs", "a.cs", "./b/c.cs", "../x.cs", @"D:\repo\a.cs", " ", "src/../d.cs"],
            Root);

        Assert.Equal(["b/c.cs", "a.cs", "d.cs"], result.Paths);
        Assert.False(result.AllOutside);
    }
}
