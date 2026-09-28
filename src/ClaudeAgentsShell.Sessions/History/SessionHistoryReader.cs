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
/// Имя сессии (<see cref="SessionSummary.Name"/>) ищется в хвосте файла тем же дескриптором
/// (<see cref="TranscriptTail"/>): у дописанного файла — только в дописанном, с переносом
/// найденного из кэша, так что имя, данное или сменённое по ходу сессии, подхватывается
/// следующим же чтением.
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

    /// <summary>
    /// Сколько байт от конца просматривается в поисках имени сессии, когда читается список.
    /// </summary>
    /// <remarks>
    /// Замер по 877 транскриптам пользователя с <c>ai-title</c> (28.09.2026): у законченной
    /// сессии последняя запись лежит не дальше 34 КБ от конца (медиана — последняя строка):
    /// при выходе Claude Code дописывает метаданные заново. Список истории — это законченные
    /// сессии, и окна в 64 КБ хватает всем; на сотне файлов оно добавляет одно чтение
    /// с диска на файл, без разбора JSON (см. <see cref="TranscriptTail"/>).
    /// </remarks>
    private const long ListTailWindow = 64 * 1024;

    /// <summary>
    /// То же для одной живой сессии (вкладка): её читают посреди работы, а не после выхода.
    /// </summary>
    /// <remarks>
    /// Между соседними записями <c>ai-title</c> одного файла (9 466 промежутков) — медиана 38 КБ,
    /// p90 75 КБ, p99 275 КБ, больше мегабайта — 37 случаев (0,4 %). Первое чтение живой сессии
    /// берёт мегабайт; дальше просматривается только дописанное, а найденное переносится
    /// из кэша, поэтому окно ограничивает лишь холодный старт. Мегабайт из кэша страниц —
    /// около миллисекунды, и он читается вне потока интерфейса.
    /// </remarks>
    private const long SingleTailWindow = 1024 * 1024;

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
                    var parsed = await ReadFileAsync(files[index], ListTailWindow, token).ConfigureAwait(false);
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
    public Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);

        // Идентификатор приходит из полезной нагрузки хука, то есть снаружи: в путь он попадает
        // только после проверки, иначе «сессия» с разделителями увела бы чтение вверх по дереву.
        if (!IsSafeSessionId(sessionId))
        {
            return Task.FromResult<SessionSummary?>(null);
        }

        string path;
        try
        {
            path = Path.Combine(ProjectDirectory(workingDirectory), sessionId + TranscriptExtension);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Task.FromResult<SessionSummary?>(null);
        }

        return ReadSingleAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SessionSummary?> ReadTranscriptAsync(string transcriptPath, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcriptPath);
        ArgumentNullException.ThrowIfNull(sessionId);

        return IsSafeSessionId(sessionId) && TryResolveTranscript(transcriptPath, sessionId, out var path)
            ? ReadSingleAsync(path, cancellationToken)
            : Task.FromResult<SessionSummary?>(null);
    }

    /// <summary>
    /// Путь из <c>transcript_path</c> годится для чтения, только если это <c>&lt;id&gt;.jsonl</c>
    /// этой же сессии внутри <c>~/.claude/projects</c>.
    /// </summary>
    /// <remarks>
    /// Путь приходит снаружи — из тела запроса к приёмнику хуков. Сравнивается нормализованный
    /// полный путь, поэтому <c>..</c> из каталога не выводит, а разделитель в конце корня
    /// отсекает соседа вроде <c>projects2</c>. Относительный путь отвергается: он разрешился бы
    /// от текущего каталога процесса. Ссылки и junction внутри каталога Claude Code не
    /// раскрываются: файл только читается, а каталог принадлежит Claude Code.
    /// </remarks>
    private bool TryResolveTranscript(string transcriptPath, string sessionId, out string path)
    {
        path = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(transcriptPath))
            {
                return false;
            }

            var full = Path.GetFullPath(transcriptPath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ClaudeProjects)) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(full), sessionId + TranscriptExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = full;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Сводка одной сессии по проверенному пути к её транскрипту.</summary>
    private async Task<SessionSummary?> ReadSingleAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            var parsed = await ReadFileAsync(file, SingleTailWindow, cancellationToken).ConfigureAwait(false);

            // Файл прочитан — сводка отдаётся даже без заголовка: это ответ «искали и не нашли».
            // Не открылся или оборвался на середине — null, и тогда спросить позже имеет смысл.
            return parsed.Readable || parsed.Summary.DisplayTitle is not null ? parsed.Summary : null;
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

    private async Task<ParsedTranscript> ReadFileAsync(FileInfo file, long tailWindow, CancellationToken cancellationToken)
    {
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var size = file.Length;

        _cache.TryGetValue(file.FullName, out var cached);
        if (cached is not null && cached.Matches(modified, size))
        {
            return cached.Parsed;
        }

        // Файл только вырос — это тот же транскрипт, дописанный Claude Code: уже просмотренное
        // не перечитывается. Уменьшился или время ушло назад — это другой файл, всё с нуля.
        var grown = cached is not null && size >= cached.Size && modified >= cached.Modified;

        // Живой транскрипт без заголовка (сессия начата слэш-командой и т.п.) меняется на каждом
        // сообщении. Если файл только вырос, начало уже просмотрено: упёрлись в предел — заголовка
        // не будет (он был бы в начале), не упёрлись — продолжаем с сохранённой позиции.
        var resume = grown && cached!.Parsed.Summary.Title is null && cached.Progress is { } progress
            ? progress
            : ScanProgress.Start;
        var headKnown = resume.LimitReached;

        // С заголовком начало файла не перечитывается, а сверяется: строка заголовка лежит на
        // прежнем месте с прежними байтами — значит, файл только дописан. Читается ровно она,
        // без разбора JSON; не совпала (Claude Code переписал файл на месте) — просмотр с нуля.
        // Без сверки каждая граница хода разбирала бы заново всё, что лежит до первого
        // сообщения, а там бывают мегабайты вложений.
        var anchor = grown && cached!.Parsed.Summary.Title is not null ? cached.Anchor : null;

        // Имя сессии «последняя запись побеждает»: у дописанного файла найденное переносится,
        // и просматривается только дописанное (см. TranscriptTail). Не совпала сверка начала —
        // перенос отменяется ниже.
        var carried = grown ? cached!.Tail : TitleRecords.None;

        HeadScan head;
        ScanProgress next;
        TitleRecords tail;
        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                useAsync: true);

            if (headKnown)
            {
                var known = cached!.Parsed;
                head = new HeadScan(known.Summary.Title, known.Summary.Branch, known.Auxiliary, Readable: true, Anchor: null);
                next = resume;
            }
            else if (anchor is not null && await anchor.MatchesAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                var known = cached!.Parsed;
                head = new HeadScan(known.Summary.Title, known.Summary.Branch, known.Auxiliary, Readable: true, anchor);
                next = ScanProgress.Start;
            }
            else
            {
                if (anchor is not null)
                {
                    // Строка заголовка не совпала: файл переписан на месте, а не дописан. Найденное
                    // в хвосте прежнего содержимого переносить нельзя — хвост тоже просматривается заново.
                    carried = TitleRecords.None;
                }

                (head, next) = await ParseAsync(stream, _scanLimit, resume, cancellationToken).ConfigureAwait(false);
            }

            // Хвост читается тем же дескриптором: второе открытие файла стоило бы дороже чтения.
            // Запусков без терминала (claude -p, SDK) в списке нет, и имени Claude Code им не даёт —
            // а в каталоге проекта их бывает больше половины. Их хвост не читается.
            tail = head.Readable && !head.Auxiliary
                ? await TranscriptTail.ScanAsync(stream, size, carried, tailWindow, cancellationToken).ConfigureAwait(false)
                : carried;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException)
        {
            // Не открылся или оборвался: деградация до «имя файла и дата», исключение наружу — нет.
            head = new HeadScan(null, null, Auxiliary: false, Readable: false, Anchor: null);
            next = ScanProgress.Start;
            tail = TitleRecords.None;
        }

        var summary = new SessionSummary(
            Path.GetFileNameWithoutExtension(file.Name),
            file.FullName,
            modified,
            size,
            head.Title,
            null,
            head.Branch,
            tail.Name);
        var parsed = new ParsedTranscript(summary, head.Readable, head.Auxiliary);

        if (parsed.Readable)
        {
            _cache[file.FullName] = new CachedSummary(modified, size, parsed, head.Title is null ? next : null, tail, head.Anchor);
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

    /// <summary>
    /// Ищет заголовок с начала файла (или с сохранённой позиции) — до первого сообщения
    /// пользователя, но не дальше предела просмотра.
    /// </summary>
    private static async Task<(HeadScan Head, ScanProgress Progress)> ParseAsync(
        FileStream stream,
        long scanLimit,
        ScanProgress resume,
        CancellationToken cancellationToken)
    {
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
        TitleAnchor? anchor = null;
        // В пул возвращается только взятый из него буфер исходного размера.
        byte[]? rented = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var buffer = rented;

        try
        {
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

                var lineStart = offset;
                var parsed = Inspect(buffer.AsSpan(start, newline), offset == 0);
                if (parsed.Title is not null)
                {
                    anchor = TitleAnchor.Of(lineStart, buffer.AsSpan(start, newline));
                }

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
        var auxiliary = AuxiliarySession.IsAuxiliary(entrypoint, sidechain);
        return (
            new HeadScan(title, branch, auxiliary, readable, readable ? anchor : null),
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

    /// <summary>Что дал просмотр начала файла.</summary>
    /// <param name="Title">Первое сообщение пользователя; <c>null</c> — не нашлось.</param>
    /// <param name="Branch">Ветка из просмотренной части.</param>
    /// <param name="Auxiliary">Транскрипт сабагента или запуска без терминала.</param>
    /// <param name="Readable"><c>false</c> — чтение оборвалось.</param>
    /// <param name="Anchor">Где лежит строка заголовка — для сверки при дописывании.</param>
    private readonly record struct HeadScan(string? Title, string? Branch, bool Auxiliary, bool Readable, TitleAnchor? Anchor);

    /// <summary>Место и отпечаток строки, из которой взят заголовок.</summary>
    /// <param name="Start">Байтовое смещение начала строки.</param>
    /// <param name="Length">Длина строки в байтах без <c>\n</c>.</param>
    /// <param name="Hash">Отпечаток байтов строки; живёт только в памяти процесса.</param>
    private sealed record TitleAnchor(long Start, int Length, int Hash)
    {
        /// <summary>Строки длиннее берутся мимо общего пула — как и расширенный буфер разбора.</summary>
        private const int PooledLimit = 64 * 1024;

        public static TitleAnchor Of(long start, ReadOnlySpan<byte> line) => new(start, line.Length, HashOf(line));

        /// <summary>Строка заголовка на прежнем месте и не менялась.</summary>
        public async Task<bool> MatchesAsync(FileStream stream, CancellationToken cancellationToken)
        {
            if (Start + Length > stream.Length)
            {
                return false;
            }

            var pooled = Length <= PooledLimit;
            var buffer = pooled ? ArrayPool<byte>.Shared.Rent(Length) : new byte[Length];
            try
            {
                stream.Seek(Start, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, Length), cancellationToken).ConfigureAwait(false);
                return HashOf(buffer.AsSpan(0, Length)) == Hash;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            finally
            {
                if (pooled)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private static int HashOf(ReadOnlySpan<byte> line)
        {
            var hash = default(HashCode);
            hash.AddBytes(line);
            return hash.ToHashCode();
        }
    }

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
    /// <param name="Tail">Найденные записи имени сессии и докуда просмотрен хвост.</param>
    /// <param name="Anchor">Только у записи с заголовком: где лежит его строка.</param>
    private sealed record CachedSummary(
        DateTimeOffset Modified,
        long Size,
        ParsedTranscript Parsed,
        ScanProgress? Progress,
        TitleRecords Tail,
        TitleAnchor? Anchor)
    {
        public bool Matches(DateTimeOffset modified, long size) => Modified == modified && Size == size;
    }
}
