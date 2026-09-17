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

    // Каталоги, на которые подписан наблюдатель. Нужен именно список путей: при повторной
    // загрузке строки заменяются, и без него прежние наблюдатели остались бы висеть.
    private readonly List<string> _watched = [];

    private ProjectRowViewModel? _selectedRow;
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

    /// <summary>
    /// Строка, выбранная пользователем. Нужна для холодного старта: пока вкладок нет,
    /// это единственный способ понять, в каком проекте открывать новую сессию.
    /// </summary>
    public ProjectRowViewModel? SelectedRow
    {
        get => _selectedRow;
        private set => SetProperty(ref _selectedRow, value);
    }

    /// <summary>Запоминает выбранную строку.</summary>
    public void Select(ProjectRowViewModel? row) => SelectedRow = row;

    /// <summary>Читает список проектов и подхватывает ветки и доступность каталогов.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var projects = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        UnwatchAll();
        SelectedRow = null;
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
    /// <returns>
    /// Добавленная строка, уже имеющаяся строка того же каталога либо <c>null</c>,
    /// если пользователь отказался от выбора.
    /// </returns>
    public async Task<ProjectRowViewModel?> AddProjectAsync(CancellationToken cancellationToken)
    {
        var path = _folderPicker.PickFolder("Каталог проекта");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Тот же каталог второй строкой — почти наверняка промах в диалоге: показываем
        // пользователю уже существующую строку вместо близнеца, который путал бы счётчики.
        if (_rows.FirstOrDefault(existing => ProjectNaming.SamePath(existing.Path, path)) is { } duplicate)
        {
            return duplicate;
        }

        var project = new ProjectDefinition(
            Guid.NewGuid(),
            ProjectNaming.DefaultNameFor(path),
            path,
            ShellKind.Pwsh,
            PreLaunch: null,
            ExtraArgs: [],
            Order: _rows.Count);

        // Сначала запись, потом строка на экране: иначе сорвавшееся сохранение оставило бы
        // в списке проект, которого нет в файле.
        var projects = _rows
            .Select((row, index) => row.Project with { Order = index })
            .Append(project)
            .ToList();

        await _store.SaveAsync(projects, cancellationToken).ConfigureAwait(true);

        var added = new ProjectRowViewModel(project);
        _rows.Add(added);
        await AttachAsync(added, cancellationToken).ConfigureAwait(true);
        return added;
    }

    /// <summary>
    /// Перепроверяет, на месте ли каталог строки. Каталог мог исчезнуть уже после загрузки
    /// списка, поэтому проверка повторяется перед каждым запуском сессии.
    /// </summary>
    public async Task<bool> RefreshAvailabilityAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.IsAvailable = await _directoryProbe.ExistsAsync(row.Path, cancellationToken).ConfigureAwait(true);
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
        UnwatchAll();
    }

    private async Task AttachAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        if (!await RefreshAvailabilityAsync(row, cancellationToken).ConfigureAwait(true))
        {
            // Каталога нет: ни ветки, ни слежения — следить не за чем.
            return;
        }

        row.Branch = await _branchReader.ReadAsync(row.Path, cancellationToken).ConfigureAwait(true);
        await _branchWatcher.WatchAsync(row.Path, cancellationToken).ConfigureAwait(true);
        _watched.Add(row.Path);
    }

    private void UnwatchAll()
    {
        foreach (var path in _watched)
        {
            _branchWatcher.Unwatch(path);
        }

        _watched.Clear();
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
