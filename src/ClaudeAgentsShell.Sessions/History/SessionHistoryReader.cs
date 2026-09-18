using System.Collections.Concurrent;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Читает транскрипты Claude Code из <c>~/.claude/projects/&lt;slug&gt;</c> (раздел 5.2 ТЗ).
/// <para>
/// Файл читается потоково и чтение прекращается, как только найден заголовок, но не позже
/// <see cref="SessionsOptions.TranscriptScanLimit"/>: транскрипты вырастают до десятков
/// мегабайт, а нужна из них одна строка. Разобранное кэшируется по ключу «путь, время
/// изменения, размер».
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
    private readonly long _scanLimit;
    private readonly ConcurrentDictionary<string, CachedSummary> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="SessionHistoryReader" />
    /// <param name="paths">Каталоги приложения и Claude Code.</param>
    /// <param name="options">Настройки слоя; отсюда берётся предел просмотра транскрипта.</param>
    public SessionHistoryReader(IAppDataPaths paths, SessionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        _paths = paths;
        _scanLimit = options.TranscriptScanLimit;
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
            // Списку сводка нужна всегда: не дочитанный и не открывшийся файл всё равно
            // показывается строкой «имя файла и дата» (раздел 8 ТЗ).
            var parsed = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
            summaries.Add(parsed.Summary);
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

            var parsed = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);

            // Файл прочитан — сводка отдаётся даже без заголовка: это ответ «искали и не нашли»,
            // по которому вызывающий перестаёт спрашивать. Не открылся или оборвался на середине —
            // null, и тогда спросить позже имеет смысл.
            return parsed.Readable || parsed.Summary.Title is not null ? parsed.Summary : null;
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

    private async Task<ParsedTranscript> ReadFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var size = file.Length;

        if (_cache.TryGetValue(file.FullName, out var cached) && cached.Matches(modified, size))
        {
            return cached.Parsed;
        }

        var parsed = await ParseAsync(file, modified, size, _scanLimit, cancellationToken).ConfigureAwait(false);
        _cache[file.FullName] = new CachedSummary(modified, size, parsed);
        return parsed;
    }

    private static async Task<ParsedTranscript> ParseAsync(
        FileInfo file,
        DateTimeOffset modified,
        long size,
        long scanLimit,
        CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        string? title = null;
        string? branch = null;
        var readable = true;

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

            var scanned = 0L;
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

                // Предел просмотра: заголовок ищется по хуку Stop, то есть на каждый ответ
                // агента, и многомегабайтный проход по горячему пути недопустим. Отсчёт идёт
                // по уже прочитанному, поэтому одна аномально длинная строка предел перешагнёт —
                // ограничить её длину, не потеряв заголовок из вставленного лога, нечем.
                scanned += line.Length + 1;
                if (scanned >= scanLimit)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException)
        {
            // Деградация до «имя файла и дата»: это допустимо, исключение наружу — нет.
            // Но отличать «прочитали и не нашли» от «прочитать не смогли» обязательно:
            // во втором случае спросить позже имеет смысл, в первом — нет.
            readable = false;
        }

        // MessageCount остаётся null умышленно: чтобы его посчитать, пришлось бы дочитать файл
        // до конца, а это прямо противоречит требованию остановиться на заголовке.
        return new ParsedTranscript(
            new SessionSummary(sessionId, file.FullName, modified, size, title, null, branch), readable);
    }

    /// <summary>Разобранный транскрипт и признак того, что файл удалось прочитать.</summary>
    /// <param name="Summary">Сводка; без заголовка, если его не нашли или чтение сорвалось.</param>
    /// <param name="Readable">
    /// <c>false</c> — файл не открылся или чтение оборвалось. Отсутствие заголовка в этом случае
    /// ничего не доказывает, и спросить позже имеет смысл.
    /// </param>
    private readonly record struct ParsedTranscript(SessionSummary Summary, bool Readable);

    private readonly record struct CachedSummary(DateTimeOffset Modified, long Size, ParsedTranscript Parsed)
    {
        public bool Matches(DateTimeOffset modified, long size) => Modified == modified && Size == size;
    }
}
