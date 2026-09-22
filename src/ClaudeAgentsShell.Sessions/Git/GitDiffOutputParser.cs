using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>Атрибуты пути из <c>git check-attr</c>, влияющие на свёртку.</summary>
/// <param name="LinguistGenerated">Значение <c>linguist-generated</c>: <c>set</c>, <c>unset</c>, <c>true</c>… или <c>null</c> — не задан.</param>
/// <param name="Diff">Значение <c>diff</c>; <c>unset</c> — это <c>-diff</c> в <c>.gitattributes</c>.</param>
public readonly record struct GitPathAttributes(string? LinguistGenerated, string? Diff)
{
    /// <summary>Атрибуты не заданы.</summary>
    public static GitPathAttributes None => default;
}

/// <summary>База сравнения, выбранная из существующих ссылок.</summary>
/// <param name="Name">Как показывать: <c>origin/main</c>, <c>main</c>.</param>
/// <param name="Revision">Что передавать в <c>merge-base</c>.</param>
public sealed record GitBaseCandidate(string Name, string Revision);

/// <summary>
/// Разбор вывода git. Чистые функции без процессов и файлов — каждая покрыта тестами отдельно.
/// Все форматы — с <c>-z</c>: пути приходят как есть, без кавычек и экранирования.
/// </summary>
public static class GitDiffOutputParser
{
    /// <summary>Ссылки, из которых выбирается база по умолчанию, в порядке предпочтения.</summary>
    public static IReadOnlyList<string> DefaultBaseRefs { get; } =
        ["refs/remotes/origin/HEAD", "refs/heads/main", "refs/heads/master"];

    /// <summary>Режет вывод <c>-z</c> на поля; хвостовой NUL не даёт пустого поля.</summary>
    public static IReadOnlyList<string> SplitNul(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var fields = output.Split('\0');
        return fields.Length > 0 && fields[^1].Length == 0 ? fields[..^1] : fields;
    }

    /// <summary>
    /// Разбирает <c>git diff --raw --numstat -z</c>: сначала записи raw
    /// (<c>:старый новый sha sha СТАТУС\0путь\0[путь\0]</c>), затем numstat
    /// (<c>+\t-\tпуть\0</c> или <c>+\t-\t\0старый\0новый\0</c> у переименования).
    /// Бинарный файл в numstat — <c>-\t-</c>, у него счётчики <c>null</c>; файл без записи numstat — 0/0.
    /// Свёртка не назначается.
    /// </summary>
    public static IReadOnlyList<DiffFileEntry> ParseRawNumstat(string output)
    {
        var fields = SplitNul(output);
        var entries = new List<DiffFileEntry>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new Dictionary<string, (int? Added, int? Deleted)>(StringComparer.Ordinal);

        var i = 0;
        while (i < fields.Count)
        {
            var field = fields[i++];
            if (field.Length == 0)
            {
                continue;
            }

            if (field[0] == ':')
            {
                var status = field[(field.LastIndexOf(' ') + 1)..];
                var letter = status.Length > 0 ? status[0] : 'M';
                var twoPaths = letter is 'R' or 'C';
                if (i >= fields.Count || (twoPaths && i + 1 >= fields.Count))
                {
                    break;
                }

                string? oldPath = twoPaths ? fields[i++] : null;
                var path = fields[i++];
                var entry = new DiffFileEntry(path, letter == 'R' ? oldPath : null, MapStatus(letter), null, null, DiffCollapseReason.None);
                if (positions.TryGetValue(path, out var existing))
                {
                    // Неслитый путь приходит дважды (U и M); оставляем одну строку.
                    entries[existing] = entry;
                }
                else
                {
                    positions[path] = entries.Count;
                    entries.Add(entry);
                }

                continue;
            }

            var parts = field.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var numstatPath = parts[2];
            if (numstatPath.Length == 0)
            {
                if (i + 1 >= fields.Count)
                {
                    break;
                }

                i++; // старый путь
                numstatPath = fields[i++];
            }

            counts[numstatPath] = (ParseCount(parts[0]), ParseCount(parts[1]));
        }

        for (var index = 0; index < entries.Count; index++)
        {
            // Нет записи numstat — строк не изменилось (так бывает с -w); бинарный пришёл бы как -\t-.
            var count = counts.TryGetValue(entries[index].Path, out var found) ? found : (0, 0);
            entries[index] = entries[index] with { AddedLines = count.Added, DeletedLines = count.Deleted };
        }

        return entries;
    }

    /// <summary>
    /// Разбирает <c>git check-attr -z</c>: тройки <c>путь\0атрибут\0значение\0</c>.
    /// Значение <c>unspecified</c> считается «не задан».
    /// </summary>
    public static IReadOnlyDictionary<string, GitPathAttributes> ParseCheckAttr(string output)
    {
        var fields = SplitNul(output);
        var result = new Dictionary<string, GitPathAttributes>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < fields.Count; i += 3)
        {
            var path = fields[i];
            var attribute = fields[i + 1];
            var value = fields[i + 2] == "unspecified" ? null : fields[i + 2];
            result.TryGetValue(path, out var current);
            result[path] = attribute switch
            {
                "linguist-generated" => current with { LinguistGenerated = value },
                "diff" => current with { Diff = value },
                _ => current,
            };
        }

        return result;
    }

    /// <summary>
    /// Выбирает базу по выводу <c>git for-each-ref --format=%(refname)%00%(symref)</c>
    /// по ссылкам <see cref="DefaultBaseRefs"/>: <c>origin/HEAD</c>, затем <c>main</c>, затем <c>master</c>.
    /// Совпадение имени точное: <c>for-each-ref</c> понимает шаблон как префикс и отдал бы <c>main/x</c>.
    /// </summary>
    /// <returns>База или <c>null</c>, если ни одной из ссылок нет.</returns>
    public static GitBaseCandidate? PickDefaultBase(string forEachRefOutput)
    {
        ArgumentNullException.ThrowIfNull(forEachRefOutput);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in forEachRefOutput.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\0');
            if (parts[0].Length > 0)
            {
                found[parts[0]] = parts.Length > 1 ? parts[1] : string.Empty;
            }
        }

        foreach (var reference in DefaultBaseRefs)
        {
            if (!found.TryGetValue(reference, out var symref))
            {
                continue;
            }

            var target = symref.Length > 0 ? symref : reference;
            return new GitBaseCandidate(ShortName(target), target);
        }

        return null;
    }

    /// <summary>
    /// Разбирает <c>git worktree list --porcelain</c> с <c>-z</c> или без: записи из полей
    /// <c>worktree</c>, <c>HEAD</c>, <c>branch</c>/<c>detached</c>, разделённые пустым полем.
    /// Голые (<c>bare</c>) и потерянные (<c>prunable</c>) деревья пропускаются — перейти в них нельзя.
    /// </summary>
    /// <param name="output">Вывод git.</param>
    /// <param name="currentRoot">Корень, в котором считался diff, — для <see cref="GitWorktree.IsCurrent"/>.</param>
    public static IReadOnlyList<GitWorktree> ParseWorktreeList(string output, string? currentRoot)
    {
        ArgumentNullException.ThrowIfNull(output);
        var separator = output.Contains('\0', StringComparison.Ordinal) ? '\0' : '\n';
        var result = new List<GitWorktree>();
        string? path = null;
        string? branch = null;
        var skip = false;

        void Flush()
        {
            if (path is not null && !skip)
            {
                var native = NormalizeDirectory(path);
                result.Add(new GitWorktree(native, branch, currentRoot is not null && SameDirectory(native, currentRoot)));
            }

            path = null;
            branch = null;
            skip = false;
        }

        foreach (var raw in output.Split(separator))
        {
            var field = raw.TrimEnd('\r');
            if (field.Length == 0)
            {
                Flush();
            }
            else if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = field["worktree ".Length..];
            }
            else if (field.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = ShortName(field["branch ".Length..]);
            }
            else if (field == "bare" || field.StartsWith("prunable", StringComparison.Ordinal))
            {
                skip = true;
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// Приводит путь, названный агентом или пользователем, к виду pathspec от корня:
    /// разделители <c>/</c>, без <c>./</c> в начале; абсолютный путь внутри корня становится
    /// относительным. <c>null</c> — путь пустой или вне корня.
    /// </summary>
    public static string? NormalizeRequestedPath(string path, string repositoryRoot)
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

    /// <summary>Каталог из вывода git (<c>D:/r</c>) в родной форме ОС (<c>D:\r</c>).</summary>
    public static string NormalizeDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Trim();
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ShortName(string reference)
    {
        foreach (var prefix in (string[])["refs/heads/", "refs/remotes/", "refs/"])
        {
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                return reference[prefix.Length..];
            }
        }

        return reference;
    }

    private static int? ParseCount(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DiffChangeKind MapStatus(char letter) => letter switch
    {
        'A' or 'C' => DiffChangeKind.Added,
        'D' => DiffChangeKind.Deleted,
        'R' => DiffChangeKind.Renamed,
        _ => DiffChangeKind.Modified,
    };
}
