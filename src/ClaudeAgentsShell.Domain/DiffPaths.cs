namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Приведение путей, названных агентом или пользователем, к виду путей оглавления diff.
/// Одно место на всех: чтение git сужает по ним оглавление, панель по ним же раскрывает файлы —
/// формы обязаны совпадать.
/// </summary>
public static class DiffPaths
{
    /// <summary>
    /// Приводит путь к виду pathspec от корня: разделители <c>/</c>, сегменты <c>.</c> и <c>..</c>
    /// схлопнуты (строково, без обращения к диску), ведущие <c>/</c> и <c>./</c> отброшены;
    /// абсолютный путь внутри корня становится относительным. <c>null</c> — путь пустой, сам корень
    /// или выходит за корень (абсолютный вне него или относительный, поднимающийся выше через <c>..</c>).
    /// </summary>
    /// <remarks>
    /// Регистр не приводится: git сравнивает pathspec с учётом регистра, и <c>SRC/b.cs</c> не
    /// найдёт <c>src/b.cs</c> даже на Windows. Без регистра сравнивается только корень у абсолютных путей.
    /// </remarks>
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
            if (Path.IsPathFullyQualified(relative))
            {
                // Другой диск: общего корня нет.
                return null;
            }

            trimmed = relative;
        }

        var segments = new List<string>();
        foreach (var segment in trimmed.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count == 0)
                    {
                        // Выше корня: git ответил бы «outside repository» и сорвал весь запрос.
                        return null;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
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
