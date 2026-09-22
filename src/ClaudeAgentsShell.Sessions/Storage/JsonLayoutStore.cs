using System.Globalization;
using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Раскладка окна в <c>layout.json</c> (issue #4). Чтение никогда не бросает: нет файла —
/// пустая раскладка; файл пустой, битый или чужой версии — тоже пустая раскладка, а сам файл
/// переносится в <c>layout.json.bak</c>, чтобы следующая запись его не затёрла. Запись
/// атомарная, как у <see cref="ProjectStore" />: временный файл рядом плюс замена.
/// </summary>
public sealed class JsonLayoutStore : ILayoutStore, IDisposable
{
    /// <summary>Версия формата файла. Другая версия читается как «раскладки нет».</summary>
    public const int CurrentVersion = 1;

    /// <summary>Суффикс файла, который прочитать не удалось: <c>layout.json.bak</c>.</summary>
    public const string BackupSuffix = ".bak";

    private const string TempSuffix = ".tmp";
    private const int StreamBufferBytes = 4096;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAppDataPaths _paths;

    // Имя временного файла детерминированное: две одновременные записи затёрли бы друг друга.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _disposed;

    /// <inheritdoc cref="JsonLayoutStore" />
    public JsonLayoutStore(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<WorkspaceLayout> LoadAsync(CancellationToken cancellationToken)
    {
        string file;
        byte[] content;

        try
        {
            file = _paths.LayoutFile;
            if (!File.Exists(file))
            {
                return WorkspaceLayout.Empty;
            }

            content = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Файл занят или недоступен — он не битый, и уносить его в .bak не за что.
            return WorkspaceLayout.Empty;
        }

        if (Parse(content) is { } layout)
        {
            return layout;
        }

        MoveAside(file);
        return WorkspaceLayout.Empty;
    }

    /// <inheritdoc />
    public async Task SaveAsync(WorkspaceLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var document = ToDto(layout);

        // Обращение к LayoutFile создаёт каталог данных, если его ещё нет.
        var file = _paths.LayoutFile;
        var temporary = file + TempSuffix;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                StreamBufferBytes,
                useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // File.Replace бросает, если файла назначения нет: первая запись — переименованием.
            if (File.Exists(file))
            {
                File.Replace(temporary, file, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, file, overwrite: true);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
    }

    /// <summary>
    /// Разбирает содержимое файла. <c>null</c> — файл целиком негоден (пустой, битый, чужая
    /// версия); отдельная битая запись внутри годного файла просто пропускается.
    /// </summary>
    private static WorkspaceLayout? Parse(byte[] content)
    {
        if (content.Length == 0)
        {
            return null;
        }

        LayoutFileDto? document;
        try
        {
            // Файл, поправленный руками в Блокноте, может начинаться с метки порядка байтов.
            var json = content.AsSpan();
            if (json.StartsWith(Utf8Bom))
            {
                json = json[Utf8Bom.Length..];
            }

            document = JsonSerializer.Deserialize<LayoutFileDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null || document.Version != CurrentVersion || document.Projects is not { } entries)
        {
            return null;
        }

        var projects = new List<ProjectLayout>(entries.Count);
        foreach (var entry in entries)
        {
            if (Map(entry) is { } project)
            {
                projects.Add(project);
            }
        }

        return new WorkspaceLayout(ParseGuid(document.ActiveProjectId), projects);
    }

    private static ProjectLayout? Map(ProjectLayoutDto? entry)
    {
        if (entry is null || ParseGuid(entry.ProjectId) is not { } projectId || entry.Tabs is not { } rawTabs)
        {
            return null;
        }

        var savedActive = entry.ActiveTabIndex ?? 0;
        var activeIndex = 0;
        var tabs = new List<TabLayout>(rawTabs.Count);

        for (var index = 0; index < rawTabs.Count; index++)
        {
            if (rawTabs[index] is not { } tab)
            {
                continue;
            }

            // Индекс активной вкладки пересчитывается по уцелевшим записям: пропуск битой
            // записи левее не должен сдвинуть выбор на соседа.
            if (index == savedActive)
            {
                activeIndex = tabs.Count;
            }

            tabs.Add(new TabLayout(NullIfBlank(tab.SessionId), NullIfBlank(tab.Title)));
        }

        return tabs.Count == 0 ? null : new ProjectLayout(projectId, activeIndex, tabs);
    }

    private static LayoutFileDto ToDto(WorkspaceLayout layout) => new()
    {
        Version = CurrentVersion,
        ActiveProjectId = layout.ActiveProjectId?.ToString("D"),
        Projects = [.. layout.Projects.Select(static project => (ProjectLayoutDto?)new ProjectLayoutDto
        {
            ProjectId = project.ProjectId.ToString("D"),
            ActiveTabIndex = project.ActiveTabIndex,
            Tabs = [.. project.Tabs.Select(static tab => (TabLayoutDto?)new TabLayoutDto
            {
                SessionId = tab.SessionId,
                Title = tab.ShortTitle,
            })],
        })],
    };

    /// <summary>
    /// Уносит негодный файл в <c>.bak</c>: следующая запись раскладки иначе затёрла бы его
    /// молча. Прежняя копия не затирается — рядом ложится копия с отметкой времени.
    /// Сбой переноса гасится: старт с пустой раскладкой важнее сохранности негодного файла.
    /// </summary>
    private static void MoveAside(string file)
    {
        try
        {
            var backup = file + BackupSuffix;
            if (File.Exists(backup))
            {
                backup = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{file}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}{BackupSuffix}");
            }

            File.Move(file, backup, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Файл остаётся на месте; следующая запись раскладки его заменит.
        }
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
