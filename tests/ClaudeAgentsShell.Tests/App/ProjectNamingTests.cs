using ClaudeAgentsShell.App.ViewModels;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Имя проекта по умолчанию и сравнение путей.</summary>
public sealed class ProjectNamingTests
{
    [Theory]
    [InlineData(@"D:\src\domovoy", "domovoy")]
    [InlineData(@"D:\src\domovoy\", "domovoy")]
    [InlineData(@"D:\src\domovoy\\", "domovoy")]
    [InlineData("D:/src/domovoy", "domovoy")]
    [InlineData(@"\\server\share\repo", "repo")]
    [InlineData(@"D:\", @"D:")]
    [InlineData("domovoy", "domovoy")]
    public void Default_name_is_the_folder_name(string path, string expected) =>
        Assert.Equal(expected, ProjectNaming.DefaultNameFor(path));

    [Theory]
    [InlineData(@"D:\src\alpha", @"D:\src\alpha\")]
    [InlineData(@"D:\src\alpha", @"d:\SRC\Alpha")]
    public void Paths_differing_only_in_case_or_a_trailing_slash_are_the_same(string left, string right) =>
        Assert.True(ProjectNaming.SamePath(left, right));

    [Fact]
    public void Different_folders_are_not_the_same() =>
        Assert.False(ProjectNaming.SamePath(@"D:\src\alpha", @"D:\src\beta"));
}
