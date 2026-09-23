using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>Чем оказался путь неотслеживаемого элемента.</summary>
public enum UntrackedKind
{
    /// <summary>Обычный файл внутри корня — читается.</summary>
    File,

    /// <summary>Каталог (вложенный репозиторий, в том числе за ссылкой) — 0/0, не читается.</summary>
    Directory,

    /// <summary>Сама ссылка — изменённый объект; её цель не открывается.</summary>
    Link,

    /// <summary>Путь вне корня.</summary>
    Outside,

    /// <summary>Путь проходит через ссылку-каталог или файл открылся за пределами корня.</summary>
    BehindLink,
}

/// <summary>Разрешённый путь неотслеживаемого элемента.</summary>
/// <param name="Kind">Чем оказался путь.</param>
/// <param name="FullPath">Полный путь на диске.</param>
/// <param name="LinkText">Текст цели ссылки у <see cref="UntrackedKind.Link"/>.</param>
public readonly record struct UntrackedTarget(UntrackedKind Kind, string FullPath, string? LinkText);

/// <summary>
/// Разрешение путей неотслеживаемых элементов одного оглавления без разыменования ссылок.
/// Проверенные каталоги запоминаются: ~1 запрос атрибутов на уникальный каталог и 1 на элемент.
/// Потокобезопасен: файлы считаются параллельно.
/// </summary>
/// <remarks>
/// Две линии защиты. Лексическая — по атрибутам компонентов пути до открытия: ссылка в конце —
/// сам объект, ссылка-каталог в середине — не читается. По handle — после открытия
/// (<see cref="IsInsideRoot"/>): итоговый путь файла обязан лежать под итоговым путём корня.
/// Вторая закрывает подмену компонента на ссылку между проверкой и открытием, а также точки
/// монтирования томов и прочие теги повторной обработки без <see cref="FileSystemInfo.LinkTarget"/>,
/// поэтому отдельно проверять бит name-surrogate не нужно. Жёсткие ссылки вне области: это тот же
/// файл, а не перенаправление, и различить их по пути нельзя.
/// </remarks>
public sealed class UntrackedPathResolver
{
    private readonly string _root;
    private readonly Lazy<string?> _finalRoot;
    private readonly ConcurrentDictionary<string, bool> _plainDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="UntrackedPathResolver" />
    /// <param name="root">Корень рабочего дерева.</param>
    public UntrackedPathResolver(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _finalRoot = new Lazy<string?>(
            () => OperatingSystem.IsWindows() ? NativeMethods.GetFinalDirectoryPath(_root) : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Разрешает путь от корня, не открывая файлов и не разыменовывая ссылок. Путь с завершающим
    /// <c>/</c> git выдаёт для вложенного репозитория — это каталог, даже если он за ссылкой.
    /// </summary>
    /// <exception cref="IOException">Компонент пути не читается (удалён, занят) — причина ОС.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на компонент пути.</exception>
    public UntrackedTarget Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var directoryEntry = path.EndsWith('/') || path.EndsWith('\\');
        var fullPath = Path.GetFullPath(Path.Combine(_root, path.TrimEnd('/', '\\')));
        var relative = Path.GetRelativePath(_root, fullPath);
        if (relative == "." || relative == ".." || Path.IsPathFullyQualified(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return new UntrackedTarget(UntrackedKind.Outside, fullPath, null);
        }

        var segments = relative.Split(Path.DirectorySeparatorChar);
        var current = _root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!IsPlainDirectory(current))
            {
                return new UntrackedTarget(UntrackedKind.BehindLink, fullPath, null);
            }
        }

        if (directoryEntry)
        {
            return new UntrackedTarget(UntrackedKind.Directory, fullPath, null);
        }

        var attributes = File.GetAttributes(fullPath);
        if (LinkTargetOf(fullPath, attributes) is { } linkTarget)
        {
            return new UntrackedTarget(UntrackedKind.Link, fullPath, linkTarget);
        }

        return new UntrackedTarget(
            (attributes & FileAttributes.Directory) != 0 ? UntrackedKind.Directory : UntrackedKind.File, fullPath, null);
    }

    /// <summary>
    /// Открытый файл на самом деле лежит под корнем: его итоговый путь (после всех ссылок)
    /// внутри итогового пути корня. Вне Windows проверки по handle нет — <c>true</c>.
    /// </summary>
    /// <exception cref="IOException">ОС не отдала итоговый путь.</exception>
    public bool IsInsideRoot(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return _finalRoot.Value is not { } finalRoot || IsWithin(finalRoot, NativeMethods.GetFinalPath(handle));
    }

    /// <summary>
    /// <paramref name="finalPath"/> строго под <paramref name="finalRoot"/>: без учёта регистра,
    /// по границе сегмента (<c>D:\repo2</c> не под <c>D:\repo</c>); сам корень — не под ним.
    /// </summary>
    public static bool IsWithin(string finalRoot, string finalPath)
    {
        ArgumentNullException.ThrowIfNull(finalRoot);
        ArgumentNullException.ThrowIfNull(finalPath);
        var root = finalRoot.TrimEnd('\\', '/');
        return finalPath.Length > root.Length + 1
            && finalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && finalPath[root.Length] is '\\' or '/';
    }

    private bool IsPlainDirectory(string directory)
    {
        if (_plainDirectories.TryGetValue(directory, out var known))
        {
            return known;
        }

        var attributes = File.GetAttributes(directory);

        // Тег повторной обработки без цели ссылки (облачный каталог, точка монтирования тома)
        // проходит здесь; если файл за ним окажется вне корня, его поймает IsInsideRoot.
        var plain = (attributes & FileAttributes.Directory) != 0 && LinkTargetOf(directory, attributes) is null;
        _plainDirectories.TryAdd(directory, plain);
        return plain;
    }

    private static string? LinkTargetOf(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget;
    }
}
