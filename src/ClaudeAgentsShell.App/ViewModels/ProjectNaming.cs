namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Разбор пути на строки, которые видит пользователь. Чистые операции над строкой:
/// диск не трогается, поэтому логика имени по умолчанию и сравнения путей проверяется тестами.
/// </summary>
public static class ProjectNaming
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Имя проекта по умолчанию — имя выбранной папки. Для корня диска остаётся сам путь:
    /// пустое имя в списке хуже некрасивого.
    /// </summary>
    public static string DefaultNameFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.TrimEnd(Separators);
        if (trimmed.Length == 0)
        {
            return path;
        }

        var index = trimmed.LastIndexOfAny(Separators);
        var name = index < 0 ? trimmed : trimmed[(index + 1)..];
        return name.Length == 0 ? trimmed : name;
    }

    /// <summary>
    /// Приводит путь к виду для сравнения: без хвостовых разделителей.
    /// Нужно, чтобы событие слежения за веткой нашло свою строку, даже если каталог
    /// записан с обратным слэшем на конце.
    /// </summary>
    public static string NormalizeForComparison(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : path.TrimEnd(Separators);

    /// <summary>Два пути указывают на один каталог.</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);
}
