using ClaudeAgentsShell.App.Services;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Подписи версии в углу окна: срез метаданных сборки и префикс <c>v</c>.</summary>
public sealed class AppVersionTextTests
{
    [Theory]
    [InlineData("0.2.1", "v0.2.1", "Версия 0.2.1")]
    [InlineData("0.2.1+1a2b3c4d", "v0.2.1", "Версия 0.2.1+1a2b3c4d")]
    [InlineData("0.0.0-dev+abc", "v0.0.0-dev", "Версия 0.0.0-dev+abc")]
    [InlineData("v1.4.0", "v1.4.0", "Версия 1.4.0")]
    [InlineData("V1.4.0+x", "v1.4.0", "Версия 1.4.0+x")]
    [InlineData("  0.3.0  ", "v0.3.0", "Версия 0.3.0")]
    public void From_CutsBuildMetadataFromLabelAndKeepsItInToolTip(string input, string label, string toolTip)
    {
        var text = AppVersionText.From(input);

        Assert.Equal(label, text.Label);
        Assert.Equal(toolTip, text.ToolTip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    public void From_EmptyVersion_ShowsZero(string? input)
    {
        var text = AppVersionText.From(input);

        Assert.Equal("v0.0.0", text.Label);
    }

    [Fact]
    public void AssemblyAppVersion_ReadsInformationalVersionOfAppAssembly()
    {
        var version = new AssemblyAppVersion(typeof(AssemblyAppVersion).Assembly);

        // Локальная сборка берёт дефолт Directory.Build.props, релиз — версию из тега;
        // в обоих случаях это не пустая строка и не безымянный 1.0.0 SDK.
        Assert.False(string.IsNullOrWhiteSpace(version.InformationalVersion));
        Assert.False(version.InformationalVersion.StartsWith("1.0.0", StringComparison.Ordinal));
    }
}
