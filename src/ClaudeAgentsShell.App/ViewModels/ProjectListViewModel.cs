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
    private readonly IProjectSettingsDialog _settingsDialog;
    private readonly IUserPrompt _prompt;
    private readonly IShellLauncher _shellLauncher;
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
        IProjectSettingsDialog settingsDialog,
        IUserPrompt prompt,
        IShellLauncher shellLauncher,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(branchReader);
        ArgumentNullException.ThrowIfNull(branchWatcher);
        ArgumentNullException.ThrowIfNull(directoryProbe);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(settingsDialog);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(shellLauncher);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _store = store;
        _branchReader = branchReader;
        _branchWatcher = branchWatcher;
        _directoryProbe = directoryProbe;
        _folderPicker = folderPicker;
        _settingsDialog = settingsDialog;
        _prompt = prompt;
        _shellLauncher = shellLauncher;
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
    /// Добавляет проект в два шага (раздел 6.5 ТЗ): сначала выбор папки, затем диалог
    /// настроек с подставленными умолчаниями — имя по папке, оболочка <c>pwsh</c>.
    /// Отказ на любом из шагов не создаёт ничего.
    /// </summary>
    /// <remarks>
    /// Выбор папки идёт первым, а не полем внутри диалога: это привычный жест кнопки «плюс»,
    /// и он позволяет узнать близнеца по каталогу до того, как пользователь потратит время
    /// на настройки.
    /// </remarks>
    /// <returns>
    /// Добавленная строка, уже имеющаяся строка того же каталога либо <c>null</c>,
    /// если пользователь отказался.
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
        if (FindByPath(path) is { } duplicate)
        {
            return duplicate;
        }

        var draft = new ProjectDefinition(
            Guid.NewGuid(),
            ProjectNaming.DefaultNameFor(path),
            path,
            ShellKind.Pwsh,
            PreLaunch: null,
            ExtraArgs: [],
            Order: _rows.Count);

        var confirmed = await _settingsDialog
            .ShowAsync(draft, ProjectSettingsPurpose.Add, cancellationToken)
            .ConfigureAwait(true);
        if (confirmed is null)
        {
            return null;
        }

        // Путь мог измениться прямо в диалоге — проверяем близнеца ещё раз, уже по итоговому.
        if (FindByPath(confirmed.Path) is { } twin)
        {
            // Здесь, в отличие от проверки по выбранной папке, пользователь уже заполнил
            // имя, оболочку и аргументы: молча выбросить их — то же, что не отработавшая
            // кнопка. Называем проект, который занимает каталог, чтобы было куда смотреть.
            _prompt.ShowError("Проект не добавлен", DuplicatePathMessage(twin, "второй не добавлен"));
            return twin;
        }

        // Идентификатор и место в списке принадлежат списку, а не диалогу.
        var project = confirmed with { Id = draft.Id, Order = _rows.Count };

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
    /// Открывает каталог строки в проводнике. Действие над строкой панели, поэтому живёт
    /// здесь, а не в корневой ViewModel: с вкладками оно не связано никак.
    /// </summary>
    /// <returns><c>false</c>, если открыть не удалось; исключения порт не бросает.</returns>
    public Task<bool> OpenFolderAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        return _shellLauncher.OpenFolderAsync(row.Path, cancellationToken);
    }

    /// <summary>
    /// Показывает диалог настроек проекта (раздел 6.5 ТЗ) и, если пользователь согласился,
    /// записывает список и обновляет строку. Отказ от диалога не меняет ничего; путь,
    /// уже занятый другой строкой, тоже не сохраняется — с сообщением, какой проект его занял.
    /// </summary>
    /// <returns><c>true</c>, если настройки сохранены.</returns>
    public async Task<bool> EditProjectAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var edited = await _settingsDialog
            .ShowAsync(row.Project, ProjectSettingsPurpose.Edit, cancellationToken)
            .ConfigureAwait(true);
        if (edited is null)
        {
            return false;
        }

        var previousPath = row.Path;
        var pathChanged = !ProjectNaming.SamePath(previousPath, edited.Path);

        // Правило «один проект на один каталог» то же, что у добавления: иначе запрещённое
        // одним путём разрешалось бы другим. Проверяется только переезд: у пользователей
        // прежних версий уже могут лежать две строки на один каталог, и отказ по неизменному
        // пути не дал бы им сменить даже имя. Саму строку при переезде поиск не найдёт —
        // её путь другой.
        if (pathChanged && FindByPath(edited.Path) is { } twin)
        {
            _prompt.ShowError("Настройки не сохранены", DuplicatePathMessage(twin, "настройки не сохранены"));
            return false;
        }

        // Идентификатор и место в списке принадлежат списку, а не диалогу: к идентификатору
        // привязаны открытые вкладки, а порядок строк диалог не видит вовсе.
        var updated = edited with { Id = row.Id, Order = row.Project.Order };

        // Сначала запись, потом строка на экране: сорвавшееся сохранение оставило бы
        // на экране настройки, которых нет в файле.
        var projects = Renumber(candidate => ReferenceEquals(candidate, row) ? updated : candidate.Project);
        await _store.SaveAsync(projects, cancellationToken).ConfigureAwait(true);

        row.Update(updated);

        if (pathChanged)
        {
            // Каталог переехал: прежнему наблюдателю следить не за чем, а ветку и доступность
            // нужно перечитать у нового каталога.
            Unwatch(previousPath);
            row.Branch = null;
            await AttachAsync(row, cancellationToken).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Убирает проект из списка. Каталог пользователя не трогается ничем: удаляется только
    /// строка <c>projects.json</c> — приложение в проект не пишет (раздел 7 CLAUDE.md).
    /// </summary>
    /// <returns><c>true</c>, если строка убрана; <c>false</c>, если такой строки уже нет.</returns>
    public async Task<bool> RemoveProjectAsync(ProjectRowViewModel row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var index = _rows.IndexOf(row);
        if (index < 0)
        {
            return false;
        }

        var projects = _rows
            .Where(candidate => !ReferenceEquals(candidate, row))
            .Select((candidate, order) => candidate.Project with { Order = order })
            .ToList();

        // Сначала запись, потом экран: не удалось записать — список в памяти остаётся целым,
        // а сообщение об ошибке показывает вызывающий.
        await _store.SaveAsync(projects, cancellationToken).ConfigureAwait(true);

        _rows.RemoveAt(index);
        Unwatch(row.Path);

        // Порядок оставшихся пересчитан: без этого в Order осталась бы дыра, и следующий
        // добавленный проект встал бы в списке не туда.
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Update(projects[i]);
        }

        return true;
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

    // Список для записи: порядок строк на экране и есть порядок в файле.
    private List<ProjectDefinition> Renumber(Func<ProjectRowViewModel, ProjectDefinition> select) =>
        _rows.Select((row, order) => select(row) with { Order = order }).ToList();

    // Строка того же каталога, если она уже есть в списке.
    private ProjectRowViewModel? FindByPath(string path) =>
        _rows.FirstOrDefault(existing => ProjectNaming.SamePath(existing.Path, path));

    // Отказ по близнецу — одна формулировка причины для добавления и редактирования, чтобы
    // тексты не разъехались; различается только исход. Называет проект, который занимает
    // каталог: пользователю есть куда смотреть.
    private static string DuplicatePathMessage(ProjectRowViewModel twin, string outcome) =>
        $"Каталог «{twin.Path}» уже открыт проектом «{twin.Name}». "
        + $"Два проекта на один каталог развели бы счётчики сессий, поэтому {outcome}.";

    private void Unwatch(string path)
    {
        // Наблюдателя снимаем ровно один раз: путь мог быть в списке только если каталог
        // существовал в момент подключения строки.
        if (_watched.Remove(path))
        {
            _branchWatcher.Unwatch(path);
        }
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
