using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Читает ветку из <c>.git/HEAD</c> без запуска <c>git</c> (раздел 6.2 ТЗ).
/// Отделённая голова, не репозиторий, исчезнувший каталог — всё это <c>null</c>, а не ошибка.
/// </summary>
public sealed class GitBranchReader : IGitBranchReader
{
    private const string RefPrefix = "ref:";
    private const string HeadsPrefix = "refs/heads/";

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var headFile = await GitHeadLocator.FindHeadFileAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (headFile is null)
        {
            return null;
        }

        string content;
        try
        {
            if (!File.Exists(headFile))
            {
                return null;
            }

            content = await File.ReadAllTextAsync(headFile, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return ParseBranch(content);
    }

    /// <summary>
    /// Разбирает содержимое <c>HEAD</c>. <c>ref: refs/heads/&lt;имя&gt;</c> даёт ветку,
    /// голый SHA отделённой головы — <c>null</c>.
    /// </summary>
    internal static string? ParseBranch(string content)
    {
        var line = content.Split('\n', 2)[0].Trim();

        if (!line.StartsWith(RefPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var reference = line[RefPrefix.Length..].Trim();
        if (!reference.StartsWith(HeadsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var branch = reference[HeadsPrefix.Length..].Trim();
        return branch.Length == 0 ? null : branch;
    }
}
