namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Приведение путей, названных агентом или пользователем, к виду путей оглавления diff.
/// Одно место на всех: чтение git сужает по ним оглавление, панель по ним же раскрывает файлы —
/// формы обязаны совпадать.
/// </summary>
public static class DiffPaths
{
    /// <summary>Приведённый путь, обозначающий сам корень — весь репозиторий.</summary>
    public const string Root = ".";

    /// <summary>
    /// Приводит путь к виду pathspec от корня: разделители <c>/</c>, сегменты <c>.</c> и <c>..</c>
    /// схлопнуты (строково, без обращения к диску), ведущие <c>/</c> и <c>./</c> отброшены;
    /// абсолютный путь внутри корня становится относительным. Сам корень (<c>.</c>, <c>src/..</c>,
    /// абсолютный путь корня) даёт <see cref="Root"/>. <c>null</c> — путь пустой или выходит за корень (абсолютный вне него или относительный, поднимающийся выше через <c>..</c>).
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

        return segments.Count == 0 ? Root : string.Join('/', segments);
    }

    /// <summary>
    /// Приводит список путей по <see cref="NormalizeRequested(string, string)"/>: пустые и лежащие
    /// вне корня отбрасываются, повторы схлопываются, порядок сохраняется. Корень среди путей
    /// снимает сужение целиком — запрос ведёт себя как пустой.
    /// </summary>
    public static RequestedPaths NormalizeRequested(IReadOnlyList<string> paths, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var named = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            named = true;
            var normalized = NormalizeRequested(path, repositoryRoot);
            if (normalized == Root)
            {
                return new RequestedPaths([], AllOutside: false);
            }

            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return new RequestedPaths(result, AllOutside: named && result.Count == 0);
    }
}

/// <summary>Запрошенные пути после приведения <see cref="DiffPaths"/>.</summary>
/// <param name="Paths">
/// Pathspec от корня без повторов, в порядке запроса. Пусто — сужения нет: весь репозиторий.
/// </param>
/// <param name="AllOutside">Пути названы, но все лежат вне корня — сужать не до чего.</param>
public sealed record RequestedPaths(IReadOnlyList<string> Paths, bool AllOutside);
