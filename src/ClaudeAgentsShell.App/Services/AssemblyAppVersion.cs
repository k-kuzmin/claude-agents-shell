using System.Reflection;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Версия из <see cref="AssemblyInformationalVersionAttribute"/> сборки. Атрибут читается
/// один раз, в конструкторе: за время жизни процесса версия не меняется.
/// </summary>
public sealed class AssemblyAppVersion : IAppVersion
{
    /// <inheritdoc cref="AssemblyAppVersion" />
    /// <param name="assembly">Сборка, чью версию показывать, — сборка приложения.</param>
    public AssemblyAppVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        InformationalVersion = Resolve(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }

    /// <inheritdoc />
    public string InformationalVersion { get; }

    /// <summary>
    /// Выбор источника версии. Атрибута может не быть у сборки, собранной в обход SDK; тогда
    /// остаётся номер из имени сборки, а без него — пустая строка, которую разберёт форматтер.
    /// </summary>
    /// <remarks>Шов для тестов: сборку без атрибута в тесте не получить.</remarks>
    internal static string Resolve(string? informationalVersion, Version? assemblyVersion) =>
        informationalVersion ?? assemblyVersion?.ToString() ?? string.Empty;
}
