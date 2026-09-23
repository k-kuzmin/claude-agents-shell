namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Приведение путей, названных агентом или пользователем, к виду путей оглавления diff.
/// Одно место на всех: чтение git сужает по ним оглавление, панель по ним же раскрывает файлы —
/// формы обязаны совпадать.
/// </summary>
public static class DiffPaths
{
    /// <summary>
    /// Приводит путь к виду pathspec от корня: разделители <c>/</c>, без <c>./</c> в начале;
    /// абсолютный путь внутри корня становится относительным. <c>null</c> — путь пустой или вне корня.
    /// </summary>
    public static string? NormalizeRequested(string path, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(trimmed))
        {
            var relative = Path.GetRelativePath(repositoryRoot, trimmed);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            {
                return null;
            }

            trimmed = relative;
        }

        trimmed = trimmed.Replace('\\', '/');
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        trimmed = trimmed.TrimStart('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Приводит список путей по <see cref="NormalizeRequested(string, string)"/>: пути вне корня
    /// отбрасываются, повторы схлопываются, порядок сохраняется.
    /// </summary>
    public static IReadOnlyList<string> NormalizeRequested(IReadOnlyList<string> paths, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalized = NormalizeRequested(path, repositoryRoot);
            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
