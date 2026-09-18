using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Список оболочек для диалога настроек проекта.</summary>
public sealed class ShellOptionsTests
{
    [Fact]
    public void Build_CoversEveryShellKind()
    {
        // Новая оболочка обязана появиться в списке сама, без правки диалога.
        var options = ShellOptions.Build(ShellKind.Pwsh, installed: null);

        Assert.Equal(Enum.GetValues<ShellKind>().Length, options.Count);
        Assert.Equal(Enum.GetValues<ShellKind>(), options.Select(option => option.Kind));
    }

    [Fact]
    public void Build_TitlesAreReadable() =>
        Assert.All(
            ShellOptions.Build(ShellKind.Pwsh, installed: null),
            option => Assert.False(string.IsNullOrWhiteSpace(option.Title)));

    [Fact]
    public void Build_UnknownCheck_MarksNothing()
    {
        // Проверка не отвечала: выдумывать недоступность нельзя.
        var options = ShellOptions.Build(ShellKind.Pwsh, installed: null);

        Assert.All(options, option => Assert.DoesNotContain(ShellOptions.MissingMark, option.Title, StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MarksOnlyMissingShells()
    {
        var options = ShellOptions.Build(ShellKind.Pwsh, [ShellKind.WindowsPowerShell, ShellKind.Cmd]);

        var pwsh = options.Single(option => option.Kind == ShellKind.Pwsh);
        var powershell = options.Single(option => option.Kind == ShellKind.WindowsPowerShell);

        Assert.EndsWith(ShellOptions.MissingMark, pwsh.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(ShellOptions.MissingMark, powershell.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NothingInstalled_MarksEverything()
    {
        var options = ShellOptions.Build(ShellKind.Pwsh, []);

        Assert.All(options, option => Assert.EndsWith(ShellOptions.MissingMark, option.Title, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ShellKind.Pwsh)]
    [InlineData(ShellKind.WindowsPowerShell)]
    [InlineData(ShellKind.Cmd)]
    public void Build_ContainsSelectedShell(ShellKind kind) =>
        Assert.Contains(ShellOptions.Build(kind, installed: null), option => option.Kind == kind);

    [Fact]
    public void Build_UnknownSelectedValue_KeepsItInTheList()
    {
        // Испорченный projects.json не должен ронять диалог и терять значение.
        var options = ShellOptions.Build((ShellKind)42, installed: null);

        var option = options.Single(candidate => candidate.Kind == (ShellKind)42);
        Assert.False(string.IsNullOrWhiteSpace(option.Title));
    }

    [Fact]
    public void TitleFor_IsReadableForEveryKind() =>
        Assert.All(
            Enum.GetValues<ShellKind>(),
            kind => Assert.False(string.IsNullOrWhiteSpace(ShellOptions.TitleFor(kind))));
}
