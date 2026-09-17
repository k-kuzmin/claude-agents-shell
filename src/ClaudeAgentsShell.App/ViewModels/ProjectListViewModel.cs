using System.Collections.ObjectModel;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Панель проектов: список строк, их ветки и доступность каталогов.
/// Вкладками не занимается — число открытых сессий ей проставляет <see cref="ShellViewModel"/>.
/// </summary>
public sealed class ProjectListViewModel : ObservableObject, IDisposable
{
    private readonly IProjectStore _store;
    private readonly IGitBranchReader _branchReader;
    private readonly IGitBranchWatcher _branchWatcher;
    private readonly IDirectoryProbe _directoryProbe;
    private readonly IFolderPicker _folderPicker;
    private readonly IUiDispatcher _dispatcher;
    private readonly ObservableCollection<ProjectRowViewModel> _rows = [];

    private bool _disposed;

    /// <inheritdoc cref="ProjectListViewModel" />
    public ProjectListViewModel(
        IProjectStore store,
        IGitBranchReader branchReader,
        IGitBranchWatcher branchWatcher,
        IDirectoryProbe directoryProbe,
        IFolderPicker folderPicker,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(branchReader);
        ArgumentNullException.ThrowIfNull(branchWatcher);
        ArgumentNullException.ThrowIfNull(directoryProbe);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _store = store;
        _branchReader = branchReader;
        _branchWatcher = branchWatcher;
        _directoryProbe = directoryProbe;
        _folderPicker = folderPicker;
        _dispatcher = dispatcher;

        Rows = new ReadOnlyObservableCollection<ProjectRowViewModel>(_rows);
        _branchWatcher.BranchChanged += OnBranchChanged;
    }

    /// <summary>Строки списка в порядке из <c>projects.json</c>.</summary>
    public ReadOnlyObservableCollection<ProjectRowViewModel> Rows { get; }

    /// <summary>Читает список проектов и подхватывает ветки и доступность каталогов.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var projects = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        _rows.Clear();
        foreach (var project in projects.OrderBy(p => p.Order))
        {
            var row = new ProjectRowViewModel(project);
            _rows.Add(row);
            await AttachAsync(row, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Добавляет проект: диалог выбора папки, имя по умолчанию — имя папки.
    /// Полноценный диалог настроек — этап M5.
    /// </summary>
    /// <returns>Добавленная строка либо <c>null</c>, если пользователь отказался от выбора.</returns>
    public async Task<ProjectRowViewModel?> AddProjectAsync(CancellationToken cancellationToken)
    {
        var path = _folderPicker.PickFolder("Каталог проекта");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var project = new ProjectDefinition(
            Guid.NewGuid(),
            ProjectNaming.DefaultNameFor(path),
            path,
            ShellKind.Pwsh,
            PreLaunch: null,
            ExtraArgs: [],
            Order: _rows.Count);

        var row = new ProjectRowViewModel(project);
        _rows.Add(row);
        await AttachAsync(row, cancellationToken).ConfigureAwait(true);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return row;
    }

    /// <summary>
    /// Перепроверяет, на месте ли каталог строки. Каталог мог исчезнуть уже после загрузки
    /// списка, поэтому проверка повторяется перед каждым запуском сессии.
    /// </summary>
    public bool RefreshAvailability(ProjectRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.IsAvailable = _directoryProbe.Exists(row.Path);
        return row.IsAvailable;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _branchWatcher.BranchChanged -= OnBranchChanged;
    }

    private async Task AttachAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        if (!RefreshAvailability(row))
        {
            // Каталога нет: ни ветки, ни слежения — следить не за чем.
            return;
        }

        row.Branch = await _branchReader.ReadAsync(row.Path, cancellationToken).ConfigureAwait(true);
        await _branchWatcher.WatchAsync(row.Path, cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var projects = _rows
            .Select((row, index) => row.Project with { Order = index })
            .ToList();

        await _store.SaveAsync(projects, cancellationToken).ConfigureAwait(true);
    }

    private void OnBranchChanged(object? sender, GitBranchChangedEventArgs e)
    {
        // Событие приходит из потока FileSystemWatcher: в поток интерфейса его переводит порт.
        _dispatcher.Post(() =>
        {
            foreach (var row in _rows)
            {
                if (ProjectNaming.SamePath(row.Path, e.WorkingDirectory))
                {
                    row.Branch = e.Branch;
                }
            }
        });
    }
}
