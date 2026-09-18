using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Список оболочек для диалога настроек проекта.</summary>
public sealed class ShellOptionsTests
{
    [Fact]
    public void All_CoversEveryShellKind()
    {
        // Новая оболочка обязана появиться в списке сама, без правки диалога.
        Assert.Equal(Enum.GetValues<ShellKind>().Length, ShellOptions.All.Count);
        Assert.Equal(Enum.GetValues<ShellKind>(), ShellOptions.All.Select(option => option.Kind));
    }

    [Fact]
    public void All_TitlesAreReadable() =>
        Assert.All(ShellOptions.All, option => Assert.False(string.IsNullOrWhiteSpace(option.Title)));

    [Theory]
    [InlineData(ShellKind.Pwsh)]
    [InlineData(ShellKind.WindowsPowerShell)]
    [InlineData(ShellKind.Cmd)]
    public void For_ReturnsElementEqualToOneFromList(ShellKind kind) =>
        Assert.Contains(ShellOptions.For(kind), ShellOptions.All);

    [Fact]
    public void For_UnknownValue_DoesNotThrow()
    {
        // Испорченный projects.json не должен ронять диалог.
        var option = ShellOptions.For((ShellKind)42);

        Assert.Equal((ShellKind)42, option.Kind);
        Assert.False(string.IsNullOrWhiteSpace(option.Title));
    }
}
