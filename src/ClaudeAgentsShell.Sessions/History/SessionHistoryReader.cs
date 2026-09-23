using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Читает транскрипты Claude Code из <c>~/.claude/projects/&lt;slug&gt;</c> (раздел 5.2 ТЗ).
/// <para>
/// Файл читается потоково и чтение прекращается, как только найден заголовок, но не позже
/// <see cref="SessionsOptions.TranscriptScanLimit"/>: транскрипты вырастают до десятков
/// мегабайт, а нужна из них одна строка. Разобранное кэшируется по ключу «путь, время
/// изменения, размер». Для файла без заголовка запоминается, докуда он просмотрен: когда живой
/// транскрипт растёт, поиск продолжается с этой позиции, а упёршийся в предел не сканируется вовсе.
/// </para>
/// <para>
/// Файлы только читаются: ни записи, ни удаления, ни создания каталога (раздел 7 CLAUDE.md).
/// Любой сбой разбора деградирует до «имя файла и дата» и не роняет приложение.
/// </para>
/// <para>
/// Транскрипты сабагентов и запусков без терминала в список не попадают (<see cref="AuxiliarySession"/>).
/// <see cref="ReadOneAsync"/> их не отсекает: его спрашивают по id уже идущей сессии из хука.
/// </para>
/// </summary>
public sealed class SessionHistoryReader : ISessionHistoryReader
{
    private const string TranscriptExtension = ".jsonl";
    private const string TranscriptPattern = "*.jsonl";
    private const int SessionIdLimit = 128;

    /// <summary>
    /// Сколько транскриптов читается одновременно. Чтение останавливается на заголовке и упирается
    /// в открытие файла и первые килобайты, а не в процессор: больше нескольких потоков не нужно.
    /// </summary>
    private const int ReadParallelism = 8;

    /// <summary>Начальный размер буфера чтения; под длинную строку он расширяется.</summary>
    private const int ReadBufferSize = 16 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

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

        string directoryPath;
        FileInfo[] files;
        try
        {
            var directory = new DirectoryInfo(ProjectDirectory(workingDirectory));
            directoryPath = directory.FullName;
            if (!directory.Exists)
            {
                // Каталога нет — история пустая, это не ошибка (раздел 8 ТЗ).
                PruneCache(directoryPath, []);
                return [];
            }

            // Время изменения и размер приходят из перечисления каталога: на неизменный файл
            // ни открытия, ни отдельного запроса метаданных не тратится — его сводка из кэша.
            // Сабагенты старой раскладки (agent-*.jsonl рядом с сессией) отсекаются по имени,
            // без открытия. Новая раскладка кладёт их в подкаталог, и сюда они не попадают.
            files = Array.FindAll(
                directory.GetFiles(TranscriptPattern),
                static file => !AuxiliarySession.IsAgentFileName(file.Name));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return [];
        }

        Array.Sort(files, static (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));

        // Списку сводка нужна всегда: не дочитанный и не открывшийся файл всё равно
        // показывается строкой «имя файла и дата» (раздел 8 ТЗ). Файлы читаются параллельно,
        // с ограничением: на сотне транскриптов последовательное чтение даёт заметную паузу,
        // а неограниченное — сотню одновременных дескрипторов. Порядок держится индексом.
        var summaries = new SessionSummary?[files.Length];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = ReadParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
                Enumerable.Range(0, files.Length),
                options,
                async (index, token) =>
                {
                    var parsed = await ReadFileAsync(files[index], token).ConfigureAwait(false);
                    // Сабагент или запуск без терминала: продолжать его через --resume нельзя.
                    // Признак прочитан тем же проходом, что и заголовок, и лежит в кэше вместе с ним.
                    summaries[index] = parsed.Auxiliary ? null : parsed.Summary;
                })
            .ConfigureAwait(false);

        PruneCache(directoryPath, files);

        var visible = new List<SessionSummary>(summaries.Length);
        foreach (var summary in summaries)
        {
            if (summary is not null)
            {
                visible.Add(summary);
            }
        }

        return visible;
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

        _cache.TryGetValue(file.FullName, out var cached);
        if (cached is not null && cached.Matches(modified, size))
        {
            return cached.Parsed;
        }

        // Живой транскрипт без заголовка (сессия начата слэш-командой и т.п.) меняется на каждом
        // сообщении. Если файл только вырос, начало уже просмотрено: упёрлись в предел — заголовка
        // не будет (он был бы в начале), не упёрлись — продолжаем с сохранённой позиции.
        // Уменьшился или время ушло назад — это другой файл, и сканирование идёт с нуля.
        var resume = cached is { Parsed.Summary.Title: null, Progress: { } progress }
                     && size >= cached.Size
                     && modified >= cached.Modified
            ? progress
            : ScanProgress.Start;

        ParsedTranscript parsed;
        ScanProgress next;
        if (resume.LimitReached)
        {
            parsed = cached!.Parsed with { Summary = cached.Parsed.Summary with { ModifiedUtc = modified, SizeBytes = size } };
            next = resume;
        }
        else
        {
            (parsed, next) = await ParseAsync(file, modified, size, _scanLimit, resume, cancellationToken).ConfigureAwait(false);
        }

        if (parsed.Readable)
        {
            _cache[file.FullName] = new CachedSummary(modified, size, parsed, parsed.Summary.Title is null ? next : null);
        }
        else
        {
            // Не открылся или оборвался — запоминать нечего: иначе однажды занятый файл так и
            // оставался бы без заголовка, пока не изменится.
            _cache.TryRemove(file.FullName, out _);
        }

        return parsed;
    }

    /// <summary>
    /// Убирает из кэша транскрипты каталога, которых в нём больше нет. Так кэш ограничен
    /// существующими файлами просмотренных проектов, а не всем, что когда-либо читалось.
    /// </summary>
    private void PruneCache(string directory, FileInfo[] present)
    {
        var alive = new HashSet<string>(present.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var file in present)
        {
            alive.Add(file.FullName);
        }

        var directoryKey = Path.TrimEndingDirectorySeparator(directory);
        foreach (var key in _cache.Keys)
        {
            if (!alive.Contains(key)
                && string.Equals(Path.GetDirectoryName(key), directoryKey, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private static async Task<(ParsedTranscript Parsed, ScanProgress Progress)> ParseAsync(
        FileInfo file,
        DateTimeOffset modified,
        long size,
        long scanLimit,
        ScanProgress resume,
        CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        string? title = null;
        var branch = resume.Branch;
        var entrypoint = resume.Entrypoint;
        var sidechain = resume.Sidechain;
        var readable = true;

        // Позиция — байтовое смещение сразу за последней целой строкой: строки режутся по байту
        // '\n', который в UTF-8 не встречается внутри многобайтовой последовательности. Незаконченная
        // последняя строка (сессия её ещё пишет) разбирается, но в позицию не входит — при
        // следующем чтении она будет прочитана целиком.
        var offset = resume.Offset;
        var scanned = resume.ScannedChars;
        var limitReached = false;
        // В пул возвращается только взятый из него буфер исходного размера.
        byte[]? rented = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var buffer = rented;

        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                useAsync: true);
            stream.Seek(offset, SeekOrigin.Begin);

            int start = 0, end = 0;
            var endOfFile = false;
            while (true)
            {
                var newline = buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (endOfFile)
                    {
                        if (end > start)
                        {
                            var tail = Inspect(buffer.AsSpan(start, end - start), offset == 0);
                            branch ??= tail.Branch;
                            entrypoint ??= tail.Entrypoint;
                            sidechain ??= tail.Sidechain;
                            title = tail.Title;
                        }

                        break;
                    }

                    // Строки в буфере нет целиком — сдвинуть остаток в начало, при нужде расширить.
                    if (start > 0)
                    {
                        buffer.AsSpan(start, end - start).CopyTo(buffer);
                        end -= start;
                        start = 0;
                    }

                    if (end == buffer.Length)
                    {
                        // Расширенный буфер берётся мимо пула и достаётся сборщику. Строки бывают
                        // в мегабайты (base64-картинки), и такие массивы, вернувшись в общий пул,
                        // оседали бы там надолго — по одному на каждое параллельное чтение.
                        var larger = new byte[buffer.Length * 2];
                        buffer.AsSpan(0, end).CopyTo(larger);
                        if (ReferenceEquals(buffer, rented))
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                            rented = null;
                        }

                        buffer = larger;
                    }

                    var read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);
                    endOfFile = read == 0;
                    end += read;
                    continue;
                }

                var parsed = Inspect(buffer.AsSpan(start, newline), offset == 0);
                start += newline + 1;
                offset += newline + 1;
                branch ??= parsed.Branch;

                // Первые встреченные значения: у главной сессии isSidechain бывает true в дальних
                // строках, а решает то, с чего транскрипт начался.
                entrypoint ??= parsed.Entrypoint;
                sidechain ??= parsed.Sidechain;

                if (parsed.Title is { } found)
                {
                    title = found;

                    // Заголовок найден — дальше файл не читаем, он может быть очень большим.
                    break;
                }

                // Предел просмотра: заголовок ищется повторно, по хуку Stop, и многомегабайтный
                // проход недопустим. Считаются символы UTF-16, а не байты — на кириллице реально
                // прочитанных байтов примерно вдвое больше (той же мерой снят и замер, на котором
                // выбрано значение предела). Отсчёт идёт по уже прочитанному, поэтому одна
                // аномально длинная строка предел перешагнёт: ограничить её длину, не потеряв
                // заголовок из вставленного лога, нечем.
                scanned += parsed.Chars + 1;
                if (scanned >= scanLimit)
                {
                    limitReached = true;
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
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // MessageCount остаётся null умышленно: чтобы его посчитать, пришлось бы дочитать файл
        // до конца, а это прямо противоречит требованию остановиться на заголовке.
        var summary = new SessionSummary(sessionId, file.FullName, modified, size, title, null, branch);
        var auxiliary = AuxiliarySession.IsAuxiliary(entrypoint, sidechain);
        return (
            new ParsedTranscript(summary, readable, auxiliary),
            new ScanProgress(offset, scanned, limitReached, branch, entrypoint, sidechain));
    }

    /// <summary>Разбирает одну строку транскрипта из байтов UTF-8.</summary>
    /// <param name="line">Байты строки без завершающего <c>\n</c>.</param>
    /// <param name="fileStart">Строка первая в файле — у неё может быть метка порядка байтов.</param>
    private static (string? Title, string? Branch, string? Entrypoint, bool? Sidechain, int Chars) Inspect(ReadOnlySpan<byte> line, bool fileStart)
    {
        if (fileStart && line.StartsWith(Utf8Bom))
        {
            line = line[Utf8Bom.Length..];
        }

        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        var text = Encoding.UTF8.GetString(line);
        var parsed = TranscriptLineParser.Parse(text);
        return (parsed.Title, parsed.Branch, parsed.Entrypoint, parsed.Sidechain, text.Length);
    }

    /// <summary>Разобранный транскрипт и признак того, что файл удалось прочитать.</summary>
    /// <param name="Summary">Сводка; без заголовка, если его не нашли или чтение сорвалось.</param>
    /// <param name="Readable">
    /// <c>false</c> — файл не открылся или чтение оборвалось. Отсутствие заголовка в этом случае
    /// ничего не доказывает, и спросить позже имеет смысл.
    /// </param>
    /// <param name="Auxiliary">
    /// Транскрипт сабагента или запуска без терминала (<see cref="AuxiliarySession"/>): в список
    /// истории не попадает. Не прочитан признак — <c>false</c>, сессия остаётся.
    /// </param>
    private readonly record struct ParsedTranscript(SessionSummary Summary, bool Readable, bool Auxiliary);

    /// <summary>Докуда просмотрен транскрипт без заголовка.</summary>
    /// <param name="Offset">Байтовое смещение сразу за последней целой просмотренной строкой.</param>
    /// <param name="ScannedChars">Сколько символов уже засчитано в предел просмотра.</param>
    /// <param name="LimitReached">Проход упёрся в предел: дальше заголовок не ищется.</param>
    /// <param name="Branch">Ветка, найденная в просмотренной части.</param>
    /// <param name="Entrypoint">Первое <c>entrypoint</c> в просмотренной части.</param>
    /// <param name="Sidechain">Первое <c>isSidechain</c> в просмотренной части.</param>
    private sealed record ScanProgress(
        long Offset,
        long ScannedChars,
        bool LimitReached,
        string? Branch,
        string? Entrypoint,
        bool? Sidechain)
    {
        public static ScanProgress Start { get; } = new(0, 0, false, null, null, null);
    }

    /// <summary>Запись кэша разбора.</summary>
    /// <param name="Modified">Время изменения файла на момент разбора.</param>
    /// <param name="Size">Размер файла на момент разбора.</param>
    /// <param name="Parsed">Результат разбора.</param>
    /// <param name="Progress">Только у записи без заголовка: откуда продолжать поиск.</param>
    private sealed record CachedSummary(DateTimeOffset Modified, long Size, ParsedTranscript Parsed, ScanProgress? Progress)
    {
        public bool Matches(DateTimeOffset modified, long size) => Modified == modified && Size == size;
    }
}
