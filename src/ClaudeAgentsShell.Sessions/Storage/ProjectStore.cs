using System.Text.Json;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Список проектов в <c>projects.json</c> (раздел 4.1 ТЗ).
/// Чтение никогда не бросает: нет файла, битый JSON, чужая версия — пустой список
/// (раздел 8 ТЗ, приложение не падает). Запись атомарная: временный файл рядом плюс замена,
/// поэтому сбой посреди записи оставляет на месте прежний файл целиком.
/// </summary>
public sealed class ProjectStore : IProjectStore, IDisposable
{
    /// <summary>Версия формата файла. Другая версия читается как «данных нет».</summary>
    public const int CurrentVersion = 1;

    private const string TempSuffix = ".tmp";
    private const int StreamBufferBytes = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAppDataPaths _paths;

    // Имя временного файла детерминированное, поэтому две одновременные записи затёрли бы
    // друг друга. Запись сериализуется здесь, а не рассчитывает на однопоточность вызывающего.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _disposed;

    /// <inheritdoc cref="ProjectStore" />
    public ProjectStore(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        ProjectsFileDto? document;

        try
        {
            var file = _paths.ProjectsFile;
            if (!File.Exists(file))
            {
                return [];
            }

            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferBytes,
                useAsync: true);

            document = await JsonSerializer
                .DeserializeAsync<ProjectsFileDto>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }

        if (document?.Projects is not { } entries || document.Version != CurrentVersion)
        {
            return [];
        }

        var projects = new List<ProjectDefinition>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            if (Map(entries[index], index) is { } project)
            {
                projects.Add(project);
            }
        }

        return projects;
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<ProjectDefinition> projects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var document = new ProjectsFileDto
        {
            Version = CurrentVersion,
            Projects = [.. projects.Select(ToDto)],
        };

        // Обращение к ProjectsFile создаёт каталог данных, если его ещё нет.
        var file = _paths.ProjectsFile;
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

            // File.Replace бросает, если файла назначения нет, а первый в жизни запуск — ровно
            // этот случай. Поэтому первая запись делается переименованием.
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

    private static ProjectDefinition? Map(ProjectEntryDto? entry, int index)
    {
        // Битая запись пропускается, разбор продолжается — как битая строка .jsonl в разделе 8 ТЗ.
        if (entry is null
            || string.IsNullOrWhiteSpace(entry.Name)
            || string.IsNullOrWhiteSpace(entry.Path)
            || !Guid.TryParse(entry.Id, out var id))
        {
            return null;
        }

        IReadOnlyList<string> extraArgs = entry.ExtraArgs is { Count: > 0 }
            ? [.. entry.ExtraArgs.Where(static a => !string.IsNullOrEmpty(a)).Select(static a => a!)]
            : [];

        return new ProjectDefinition(
            id,
            entry.Name,
            entry.Path,
            ShellKindNames.FromName(entry.Shell),
            string.IsNullOrWhiteSpace(entry.PreLaunch) ? null : entry.PreLaunch,
            extraArgs,
            entry.Order ?? index);
    }

    private static ProjectEntryDto ToDto(ProjectDefinition project) => new()
    {
        Id = project.Id.ToString("D"),
        Name = project.Name,
        Path = project.Path,
        Shell = ShellKindNames.ToName(project.Shell),
        PreLaunch = string.IsNullOrWhiteSpace(project.PreLaunch) ? null : project.PreLaunch,
        ExtraArgs = project.ExtraArgs is null ? [] : [.. project.ExtraArgs],
        Order = project.Order,
    };
}
