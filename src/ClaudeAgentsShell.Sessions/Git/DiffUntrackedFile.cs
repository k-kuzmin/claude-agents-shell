using System.Buffers;
using System.Text;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>Счётчики строк нового файла.</summary>
/// <param name="Added">Строк в файле; <c>null</c> — бинарный или больше потолка.</param>
/// <param name="Deleted">Всегда 0, кроме бинарного — у него <c>null</c>.</param>
public readonly record struct DiffLineCounts(int? Added, int? Deleted);

/// <summary>Diff нового файла, собранный из его содержимого.</summary>
/// <param name="Text">Unified diff «новый файл».</param>
/// <param name="Truncated">Файл больше потолка: показано начало до целой строки.</param>
public readonly record struct UntrackedFileDiff(string Text, bool Truncated);

/// <summary>
/// Неотслеживаемый файл: строки и diff считаются в процессе, без запуска git на каждый файл.
/// Файл только читается; разделяемый доступ не мешает редактору и агенту писать в него.
/// </summary>
/// <remarks>
/// Ссылка (symlink, junction) — сама изменённый объект, как у git: её цель не открывается,
/// diff — одна строка с текстом цели. Путь, проходящий через ссылку-каталог, и путь вне корня
/// не читаются вовсе: иначе в панель и агенту ушло бы содержимое файла вне репозитория.
/// </remarks>
public static class DiffUntrackedFile
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Считает строки: NUL в первых <see cref="GitDiffOptions.BinarySniffBytes"/> — бинарный
    /// (<c>null</c>/<c>null</c>), больше <see cref="GitDiffOptions.FileOutputCeilingBytes"/> или
    /// не читается — <c>null</c>/0. Каталог (вложенный репозиторий) — 0/0. Ссылка — 1/0 (строка с целью),
    /// путь через ссылку-каталог или вне корня — <c>null</c>/0.
    /// Файл читается потоком фиксированным буфером, целиком в памяти не держится.
    /// </summary>
    /// <param name="root">Корень рабочего дерева.</param>
    /// <param name="path">Путь от корня.</param>
    /// <param name="options">Потолок и размер проверки на бинарность.</param>
    /// <param name="budget">
    /// Общий на оглавление бюджет чтения. Не хватило — строки не считаются (<c>null</c>/0),
    /// читается только начало файла для проверки на бинарность.
    /// </param>
    /// <param name="cancellationToken">Отмена чтения.</param>
    public static async Task<DiffLineCounts> CountLinesAsync(
        string root, string path, GitDiffOptions options, DiffReadBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);

        var target = Resolve(root, path);
        if (target.LinkText is not null)
        {
            return new DiffLineCounts(1, 0);
        }

        if (target.FullPath is not { } fullPath)
        {
            return new DiffLineCounts(null, 0);
        }

        if (Directory.Exists(fullPath))
        {
            return new DiffLineCounts(0, 0);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var stream = OpenRead(fullPath);
            if (stream.Length > options.FileOutputCeilingBytes || !budget.TryReserve(stream.Length))
            {
                // Строки не считаются, но бинарный остаётся бинарным: хватает начала файла.
                var sniffLimit = Math.Min(ChunkSize, options.BinarySniffBytes);
                var sniffed = await stream.ReadAtLeastAsync(buffer.AsMemory(0, sniffLimit), sniffLimit, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                return ContainsNul(buffer, sniffed) ? new DiffLineCounts(null, null) : new DiffLineCounts(null, 0);
            }

            long position = 0;
            var lines = 0;
            var last = (byte)'\n';
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var sniffLength = position < options.BinarySniffBytes ? (int)Math.Min(read, options.BinarySniffBytes - position) : 0;
                if (ContainsNul(buffer, sniffLength))
                {
                    return new DiffLineCounts(null, null);
                }

                lines += CountNewLines(buffer, read);
                last = buffer[read - 1];
                position += read;
                if (position > options.FileOutputCeilingBytes)
                {
                    // Файл дорос, пока его читали.
                    return new DiffLineCounts(null, 0);
                }
            }

            return new DiffLineCounts(position > 0 && last != '\n' ? lines + 1 : lines, 0);
        }
        catch (IOException)
        {
            return new DiffLineCounts(null, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new DiffLineCounts(null, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Собирает unified diff «новый файл» так же, как его напечатал бы git для добавленного файла.
    /// Больше потолка — начало до последней целой строки и <see cref="UntrackedFileDiff.Truncated"/>.
    /// </summary>
    /// <param name="root">Корень рабочего дерева.</param>
    /// <param name="path">Путь от корня через <c>/</c> — и для заголовков.</param>
    /// <param name="options">Потолок и размер проверки на бинарность.</param>
    /// <param name="cancellationToken">Отмена чтения.</param>
    /// <exception cref="IOException">Файл не читается, лежит вне корня или за ссылкой-каталогом.</exception>
    public static async Task<UntrackedFileDiff> BuildDiffAsync(string root, string path, GitDiffOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);

        var target = Resolve(root, path);
        if (target.LinkText is { } linkText)
        {
            return new UntrackedFileDiff(LinkDiff(path, linkText), false);
        }

        var fullPath = target.FullPath ?? throw new IOException("Файл вне репозитория или за ссылкой: " + path);
        var header = new StringBuilder()
            .Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
            .Append("new file mode 100644\n");

        if (Directory.Exists(fullPath))
        {
            return new UntrackedFileDiff(header.ToString(), false);
        }

        var (content, truncated) = await ReadHeadAsync(fullPath, options.FileOutputCeilingBytes, cancellationToken).ConfigureAwait(false);
        if (content.AsSpan(0, Math.Min(content.Length, options.BinarySniffBytes)).Contains((byte)0))
        {
            header.Append("Binary files /dev/null and b/").Append(path).Append(" differ\n");
            return new UntrackedFileDiff(header.ToString(), false);
        }

        var length = content.Length;
        if (truncated)
        {
            var lastNewLine = Array.LastIndexOf(content, (byte)'\n');
            length = lastNewLine + 1;
        }

        if (length == 0)
        {
            return new UntrackedFileDiff(header.ToString(), truncated);
        }

        var text = Encoding.UTF8.GetString(content, 0, length);
        var endsWithNewLine = text[^1] == '\n';
        var lines = (endsWithNewLine ? text[..^1] : text).Split('\n');

        header.Append("--- /dev/null\n")
            .Append("+++ b/").Append(path).Append('\n')
            .Append("@@ -0,0 +1").Append(lines.Length == 1 ? string.Empty : "," + lines.Length).Append(" @@\n");
        foreach (var line in lines)
        {
            header.Append('+').Append(line).Append('\n');
        }

        if (!endsWithNewLine)
        {
            header.Append("\\ No newline at end of file\n");
        }

        return new UntrackedFileDiff(header.ToString(), truncated);
    }

    private static async Task<(byte[] Content, bool Truncated)> ReadHeadAsync(string fullPath, int ceiling, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(fullPath);
        var size = (int)Math.Min(stream.Length, ceiling);
        var content = new byte[size];
        var filled = 0;
        while (filled < size)
        {
            var read = await stream.ReadAsync(content.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        var truncated = stream.Length > ceiling;
        return (filled == size ? content : content[..filled], truncated);
    }

    /// <summary>
    /// Куда смотрит путь от корня, не разыменовывая ссылок: обычный файл (<see cref="UntrackedTarget.FullPath"/>),
    /// сама ссылка (<see cref="UntrackedTarget.LinkText"/>) или ничего — путь вне корня, проходит через
    /// ссылку-каталог или атрибуты не читаются. Точка повторной обработки без цели ссылки (облачный
    /// файл и т. п.) — обычный файл: её содержимое — она сама.
    /// </summary>
    private static UntrackedTarget Resolve(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, path));
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative == "." || Path.IsPathFullyQualified(relative)
                || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return default;
            }

            var segments = relative.Split(Path.DirectorySeparatorChar);
            var current = fullRoot;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    continue;
                }

                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not { } linkTarget)
                {
                    continue;
                }

                return i == segments.Length - 1 ? new UntrackedTarget(null, linkTarget) : default;
            }

            return new UntrackedTarget(fullPath, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>Diff новой ссылки, как его печатает git: режим 120000, одна строка с целью без перевода строки.</summary>
    private static string LinkDiff(string path, string linkTarget) => new StringBuilder()
        .Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
        .Append("new file mode 120000\n")
        .Append("--- /dev/null\n")
        .Append("+++ b/").Append(path).Append('\n')
        .Append("@@ -0,0 +1 @@\n")
        .Append('+').Append(linkTarget.Replace('\n', ' ')).Append('\n')
        .Append("\\ No newline at end of file\n")
        .ToString();

    private static bool ContainsNul(byte[] buffer, int length) => buffer.AsSpan(0, length).Contains((byte)0);

    private static int CountNewLines(byte[] buffer, int length) => buffer.AsSpan(0, length).Count((byte)'\n');

    private static FileStream OpenRead(string fullPath) => new(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        ChunkSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}

/// <summary>Разрешённый путь неотслеживаемого элемента; оба <c>null</c> — не читается.</summary>
/// <param name="FullPath">Обычный файл или каталог на диске.</param>
/// <param name="LinkText">Элемент — ссылка; текст её цели.</param>
internal readonly record struct UntrackedTarget(string? FullPath, string? LinkText);
