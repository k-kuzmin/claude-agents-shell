namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Находит файл <c>HEAD</c> рабочего каталога. Общий для чтения и слежения: иначе
/// наблюдатель следил бы за <c>.git</c>-файлом worktree, а читатель — за настоящим
/// каталогом git, и они разошлись бы ровно на том случае, который встречается чаще всего.
/// </summary>
internal static class GitHeadLocator
{
    private const string GitEntryName = ".git";
    private const string HeadFileName = "HEAD";
    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// Путь к <c>HEAD</c> либо <c>null</c>, если каталог не репозиторий или недоступен.
    /// В worktree и подмодулях <c>.git</c> — файл со строкой <c>gitdir: &lt;путь&gt;</c>,
    /// и <c>HEAD</c> лежит уже там; путь бывает и относительным.
    /// </summary>
    public static async Task<string?> FindHeadFileAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(workingDirectory);
            var gitEntry = Path.Combine(root, GitEntryName);

            if (Directory.Exists(gitEntry))
            {
                return Path.Combine(gitEntry, HeadFileName);
            }

            if (!File.Exists(gitEntry))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(gitEntry, cancellationToken).ConfigureAwait(false);
            var gitDirectory = ParseGitDir(content, root);
            return gitDirectory is null ? null : Path.Combine(gitDirectory, HeadFileName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? ParseGitDir(string content, string root)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[GitDirPrefix.Length..].Trim();
            if (value.Length == 0)
            {
                return null;
            }

            return Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(root, value));
        }

        return null;
    }
}
