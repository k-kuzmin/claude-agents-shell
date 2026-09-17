using System.Collections.Concurrent;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Читает транскрипты Claude Code из <c>~/.claude/projects/&lt;slug&gt;</c> (раздел 5.2 ТЗ).
/// <para>
/// Файл читается потоково и чтение прекращается, как только найден заголовок: транскрипты
/// вырастают до десятков мегабайт, а нужна из них одна строка. Разобранное кэшируется
/// по ключу «путь, время изменения, размер».
/// </para>
/// <para>
/// Файлы только читаются: ни записи, ни удаления, ни создания каталога (раздел 7 CLAUDE.md).
/// Любой сбой разбора деградирует до «имя файла и дата» и не роняет приложение.
/// </para>
/// </summary>
public sealed class SessionHistoryReader : ISessionHistoryReader
{
    private const string TranscriptExtension = ".jsonl";
    private const string TranscriptPattern = "*.jsonl";
    private const int SessionIdLimit = 128;

    private readonly IAppDataPaths _paths;
    private readonly ConcurrentDictionary<string, CachedSummary> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="SessionHistoryReader" />
    public SessionHistoryReader(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        FileInfo[] files;
        try
        {
            var directory = new DirectoryInfo(ProjectDirectory(workingDirectory));
            if (!directory.Exists)
            {
                // Каталога нет — история пустая, это не ошибка (раздел 8 ТЗ).
                return [];
            }

            files = directory.GetFiles(TranscriptPattern);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return [];
        }

        var summaries = new List<SessionSummary>(files.Length);
        foreach (var file in files.OrderByDescending(static f => f.LastWriteTimeUtc))
        {
            summaries.Add(await ReadFileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);

        // Идентификатор приходит из полезной нагрузки хука, то есть снаружи: в путь он попадает
        // только после проверки, иначе «сессия» с разделителями увела бы чтение вверх по дереву.
        if (!IsSafeSessionId(sessionId))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(Path.Combine(ProjectDirectory(workingDirectory), sessionId + TranscriptExtension));
            if (!file.Exists)
            {
                return null;
            }

            return await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    private string ProjectDirectory(string workingDirectory) =>
        Path.Combine(_paths.ClaudeProjects, SessionSlug.From(workingDirectory));

    private static bool IsSafeSessionId(string sessionId)
    {
        if (sessionId.Length is 0 or > SessionIdLimit)
        {
            return false;
        }

        foreach (var symbol in sessionId)
        {
            var allowed = symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<SessionSummary> ReadFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var size = file.Length;

        if (_cache.TryGetValue(file.FullName, out var cached) && cached.Matches(modified, size))
        {
            return cached.Summary;
        }

        var summary = await ParseAsync(file, modified, size, cancellationToken).ConfigureAwait(false);
        _cache[file.FullName] = new CachedSummary(modified, size, summary);
        return summary;
    }

    private static async Task<SessionSummary> ParseAsync(
        FileInfo file,
        DateTimeOffset modified,
        long size,
        CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        string? title = null;
        string? branch = null;

        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);

            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var parsed = TranscriptLineParser.Parse(line);
                branch ??= parsed.Branch;

                if (parsed.Title is { } found)
                {
                    title = found;

                    // Заголовок найден — дальше файл не читаем, он может быть очень большим.
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException)
        {
            // Деградация до «имя файла и дата»: это допустимо, исключение наружу — нет.
        }

        // MessageCount остаётся null умышленно: чтобы его посчитать, пришлось бы дочитать файл
        // до конца, а это прямо противоречит требованию остановиться на заголовке.
        return new SessionSummary(sessionId, file.FullName, modified, size, title, null, branch);
    }

    private readonly record struct CachedSummary(DateTimeOffset Modified, long Size, SessionSummary Summary)
    {
        public bool Matches(DateTimeOffset modified, long size) => Modified == modified && Size == size;
    }
}
