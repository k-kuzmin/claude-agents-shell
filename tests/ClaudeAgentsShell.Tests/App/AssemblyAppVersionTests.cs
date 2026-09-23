using System.Reflection;
using ClaudeAgentsShell.App.Services;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Чтение версии из сборки и откат, когда атрибута нет.</summary>
public sealed class AssemblyAppVersionTests
{
    [Fact]
    public void Reads_informational_version_attribute_of_the_assembly()
    {
        var assembly = typeof(AssemblyAppVersion).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var version = new AssemblyAppVersion(assembly);

        Assert.Equal(expected, version.InformationalVersion);
    }

    [Fact]
    public void Informational_version_wins_over_assembly_version()
    {
        Assert.Equal("0.2.1+abc", AssemblyAppVersion.Resolve("0.2.1+abc", new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void Without_attribute_falls_back_to_assembly_version()
    {
        Assert.Equal("1.2.3.4", AssemblyAppVersion.Resolve(null, new Version(1, 2, 3, 4)));
    }

    [Fact]
    public void Without_attribute_and_assembly_version_yields_empty_string()
    {
        Assert.Equal(string.Empty, AssemblyAppVersion.Resolve(null, null));
    }
}
