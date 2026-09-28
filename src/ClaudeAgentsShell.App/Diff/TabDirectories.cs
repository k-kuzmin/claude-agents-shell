using System.IO;
using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>Каталог, от которого панель вкладки (diff и файлы) считает пути агента.</summary>
internal static class TabDirectories
{
    /// <summary>Каталог вкладки: текущий каталог главного агента, до первого хука — каталог запуска.</summary>
    public static string Of(TabViewModel tab) =>
        NullIfBlank(tab.CurrentDirectory) ?? tab.WorkingDirectory;

    /// <summary>
    /// Каталог из аргументов агента. Относительный считается от каталога вкладки: иначе он
    /// разрешился бы от рабочего каталога приложения. Пустой — каталог вкладки.
    /// </summary>
    public static string Resolve(string? requested, string tabDirectory)
    {
        if (NullIfBlank(requested) is not { } directory)
        {
            return tabDirectory;
        }

        if (Path.IsPathRooted(directory))
        {
            return directory;
        }

        var combined = Path.Combine(tabDirectory, directory);
        try
        {
            return Path.GetFullPath(combined);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return combined;
        }
    }

    /// <summary>Строка агента без пустых значений: из JSON приходят и <c>null</c>, и пробелы.</summary>
    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
