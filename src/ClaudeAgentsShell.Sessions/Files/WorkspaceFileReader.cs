using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Sessions.Git;

namespace ClaudeAgentsShell.Sessions.Files;

/// <summary>
/// Чтение файлов рабочего каталога для <c>show_file</c>. Файлы только открываются на чтение
/// с разделяемым доступом: редактору и агенту это писать не мешает.
/// </summary>
/// <remarks>
/// <para>
/// Корень ищется подъёмом от каталога до первого уровня с <c>.git</c> (каталог или файл
/// worktree/подмодуля) — через <see cref="GitHeadLocator"/>, без запуска git. Так корень
/// остаётся в том же написании, что и каталог сессии: <c>rev-parse --show-toplevel</c> отдаёт
/// путь после ссылок и <c>subst</c>, и лексическая проверка «внутри корня» отвергла бы
/// законные файлы.
/// </para>
/// <para>
/// Две линии защиты, как у неотслеживаемых файлов diff: лексическая (полный путь после
/// <c>..</c> под корнем) и по handle после открытия
/// (<see cref="UntrackedPathResolver.IsInsideRoot"/>) — итоговый путь файла после всех ссылок
/// и junction обязан лежать под итоговым путём корня. Ссылки, ведущие внутрь корня, читаются.
/// </para>
/// </remarks>
public sealed class WorkspaceFileReader : IWorkspaceFileReader
{
    private const int ReadChunk = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UTF32Encoding Utf32BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);

    private readonly GitDiffOptions _options;

    /// <inheritdoc cref="WorkspaceFileReader" />
    /// <param name="options">Потолок размера и объём проверки на бинарность — те же, что у diff.</param>
    public WorkspaceFileReader(GitDiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public Task<string?> ResolveRootAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        // Task.Run: запросы атрибутов синхронны, а вызывающий может быть в потоке интерфейса.
        return Task.Run(() => ResolveRootCoreAsync(directory, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ViewedFile> ReadAsync(string root, string directory, ShowFileItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() => ReadCoreAsync(root, directory, item, cancellationToken), cancellationToken);
    }

    private static async Task<string?> ResolveRootCoreAsync(string directory, CancellationToken cancellationToken)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            full = TrimEnd(Path.GetFullPath(directory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await GitHeadLocator.FindHeadFileAsync(current, cancellationToken).ConfigureAwait(false) is not null)
            {
                return TrimEnd(current);
            }
        }

        return full;
    }

    private async Task<ViewedFile> ReadCoreAsync(string root, string directory, ShowFileItem item, CancellationToken cancellationToken)
    {
        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = TrimEnd(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(directory), item.Path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return Problem(item.Path, item, ViewedFileProblem.NotFound);
        }

        if (string.Equals(TrimEnd(fullPath), fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Сам корень — каталог.
            return Problem(".", item, ViewedFileProblem.NotFound);
        }

        if (!UntrackedPathResolver.IsWithin(fullRoot, fullPath))
        {
            return Problem(item.Path, item, ViewedFileProblem.OutsideRoot);
        }

        var relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');

        try
        {
            // Каталог открылся бы с UnauthorizedAccessException и сошёл бы за «нет прав».
            if (!File.Exists(fullPath))
            {
                return Problem(relative, item, ViewedFileProblem.NotFound);
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadChunk,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!new UntrackedPathResolver(fullRoot).IsInsideRoot(stream.SafeFileHandle))
            {
                return Problem(relative, item, ViewedFileProblem.OutsideRoot);
            }

            var ceiling = _options.FileOutputCeilingBytes;
            if (stream.Length > ceiling)
            {
                return Problem(relative, item, ViewedFileProblem.TooLarge);
            }

            var (bytes, length) = await ReadUpToAsync(stream, ceiling, cancellationToken).ConfigureAwait(false);
            if (length > ceiling)
            {
                // Файл дорос, пока его читали.
                return Problem(relative, item, ViewedFileProblem.TooLarge);
            }

            var text = Decode(bytes, length, _options.BinarySniffBytes);
            return text is null
                ? Problem(relative, item, ViewedFileProblem.Binary)
                : new ViewedFile(relative, text, item.Focus, ViewedFileProblem.None);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Problem(relative, item, ViewedFileProblem.NotFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Problem(relative, item, ViewedFileProblem.Unreadable);
        }
    }

    /// <summary>Читает до конца или до <paramref name="ceiling"/> + 1 байта — сверх потолка дальше не нужно.</summary>
    private static async Task<(byte[] Bytes, int Length)> ReadUpToAsync(FileStream stream, int ceiling, CancellationToken cancellationToken)
    {
        var limit = ceiling + 1;
        var buffer = new byte[(int)Math.Min(Math.Max(stream.Length + 1, 1), limit)];
        var filled = 0;
        while (filled < limit)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, limit));
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return (buffer, filled);
    }

    /// <summary>
    /// Текст по BOM, иначе UTF-8; невалидные байты — заменяющий символ. BOM проверяется до
    /// поиска NUL: у UTF-16 нули в начале законны. <c>null</c> — двоичный файл.
    /// </summary>
    private static string? Decode(byte[] bytes, int length, int sniffBytes)
    {
        var span = bytes.AsSpan(0, length);
        if (span.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return Encoding.UTF32.GetString(span[4..]);
        }

        if (span.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return Utf32BigEndian.GetString(span[4..]);
        }

        if (span.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(span[2..]);
        }

        if (span.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }

        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            span = span[3..];
        }

        return span[..Math.Min(span.Length, sniffBytes)].Contains((byte)0) ? null : Utf8.GetString(span);
    }

    private static ViewedFile Problem(string path, ShowFileItem item, ViewedFileProblem problem) =>
        new(path.Replace('\\', '/'), null, item.Focus, problem);

    private static string TrimEnd(string path) => Path.TrimEndingDirectorySeparator(path);
}
