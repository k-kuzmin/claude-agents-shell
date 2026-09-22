using System.Windows.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Диалог добавления и настроек проекта (раздел 6.5 ТЗ): путь, отображаемое имя, оболочка,
/// команда перед запуском и дополнительные аргументы. Ничего не сохраняет — только собирает
/// <see cref="ProjectDefinition"/>, который забирает вызывающий код.
/// </summary>
/// <remarks>
/// Идентификатор и порядок берутся у исходного описания и не меняются: диалог редактирует
/// поля, а не место проекта в списке.
/// </remarks>
public sealed class ProjectSettingsViewModel : ObservableObject
{
    /// <summary>Имя файла инструкций проекта; показывается справочно и не читается.</summary>
    public const string ClaudeMdFileName = "CLAUDE.md";

    /// <summary>Имя файла настроек MCP; показывается справочно и не читается.</summary>
    public const string McpJsonFileName = ".mcp.json";

    private readonly ProjectDefinition _original;
    private readonly ProjectSettingsPurpose _purpose;
    private readonly IFolderPicker _folderPicker;
    private readonly IDirectoryProbe _directoryProbe;
    private readonly IFileProbe _fileProbe;
    private readonly IShellAvailability _shellAvailability;

    private string _projectPath;
    private string _name;
    private ShellKind _selectedShell;
    private IReadOnlyList<ShellOption> _shells;
    private string _preLaunch;
    private string _extraArgsText;

    private ProbeResult _directoryProbeResult = ProbeResult.Unchecked;
    private ProbeResult _claudeMdProbeResult = ProbeResult.Unchecked;
    private ProbeResult _mcpJsonProbeResult = ProbeResult.Unchecked;

    // Найденные в системе оболочки: null — проверка ещё не отвечала либо не удалась.
    private IReadOnlyList<ShellKind>? _installedShells;
    private bool _shellsProbed;

    // Поколение проверки: пользователь правит путь быстрее, чем отвечает сетевая шара,
    // и результат устаревшей проверки не должен перебить более свежую.
    private int _probeGeneration;

    /// <inheritdoc cref="ProjectSettingsViewModel" />
    /// <param name="project">Что редактируем либо заготовка нового проекта.</param>
    /// <param name="purpose">Добавление или правка: меняет только подписи окна и кнопки.</param>
    /// <param name="folderPicker">Выбор каталога.</param>
    /// <param name="directoryProbe">Проверка существования каталога.</param>
    /// <param name="fileProbe">Проверка наличия <c>CLAUDE.md</c> и <c>.mcp.json</c>.</param>
    /// <param name="shellAvailability">Какие оболочки установлены в системе.</param>
    public ProjectSettingsViewModel(
        ProjectDefinition project,
        ProjectSettingsPurpose purpose,
        IFolderPicker folderPicker,
        IDirectoryProbe directoryProbe,
        IFileProbe fileProbe,
        IShellAvailability shellAvailability)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(directoryProbe);
        ArgumentNullException.ThrowIfNull(fileProbe);
        ArgumentNullException.ThrowIfNull(shellAvailability);

        _original = project;
        _purpose = purpose;
        _folderPicker = folderPicker;
        _directoryProbe = directoryProbe;
        _fileProbe = fileProbe;
        _shellAvailability = shellAvailability;

        _projectPath = project.Path ?? string.Empty;
        _name = project.Name ?? string.Empty;
        _selectedShell = project.Shell;
        _shells = ShellOptions.Build(project.Shell, installed: null);
        _preLaunch = project.PreLaunch ?? string.Empty;
        _extraArgsText = ExtraArgsSyntax.Format(project.ExtraArgs);

        BrowseCommand = new AsyncRelayCommand(_ => BrowseAsync(CancellationToken.None));
        SaveCommand = new RelayCommand(_ => Save(), _ => IsValid);
    }

    /// <summary>Диалог просит закрыть себя. <see cref="Result"/> уже выставлен.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Что вернуть вызывающему коду. <c>null</c> — пользователь ещё не нажал «сохранить»
    /// либо отказался; отказ штатен.
    /// </summary>
    public ProjectDefinition? Result { get; private set; }

    /// <summary>Заголовок окна: добавление и правка — разные действия, и путать их незачем.</summary>
    public string WindowTitle =>
        _purpose == ProjectSettingsPurpose.Add ? "Новый проект" : "Настройки проекта";

    /// <summary>Подпись главной кнопки.</summary>
    public string CommitButtonText =>
        _purpose == ProjectSettingsPurpose.Add ? "Добавить" : "Сохранить";

    /// <summary>
    /// Оболочки, доступные для выбора. Список пересобирается целиком, когда отвечает
    /// проверка установленных оболочек, — вместе с пометками у ненайденных.
    /// </summary>
    public IReadOnlyList<ShellOption> Shells => _shells;

    /// <summary>Открывает системный выбор папки.</summary>
    public ICommand BrowseCommand { get; }

    /// <summary>Закрывает диалог с результатом. Недоступна, пока поля не заполнены.</summary>
    public ICommand SaveCommand { get; }

    /// <summary>Рабочий каталог сессий.</summary>
    public string ProjectPath
    {
        get => _projectPath;
        set
        {
            if (!SetProperty(ref _projectPath, value ?? string.Empty))
            {
                return;
            }

            // Прежние результаты относятся к прежнему пути. Оставить их на экране значило бы
            // соврать: «каталог не найден» про каталог, который никто не проверял.
            ResetProbeResults();
            RaiseValidity();
        }
    }

    /// <summary>Отображаемое имя в панели проектов.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                RaiseValidity();
            }
        }
    }

    /// <summary>Выбранная оболочка.</summary>
    /// <remarks>
    /// Хранится вид оболочки, а не строка списка: список пересобирается, когда отвечает
    /// проверка, и выбор обязан это пережить.
    /// </remarks>
    public ShellOption Shell
    {
        get => _shells.FirstOrDefault(option => option.Kind == _selectedShell) ?? _shells[0];
        set
        {
            // При смене списка WPF на мгновение отдаёт сюда null. Это не выбор пользователя,
            // и подменять им оболочку проекта нельзя: молчаливая подмена — ровно то, чего
            // требует не допускать раздел 8 ТЗ.
            if (value is null || value.Kind == _selectedShell)
            {
                return;
            }

            _selectedShell = value.Kind;
            Raise();
            RaiseShellWarning();
        }
    }

    /// <summary>Команда, выполняемая в PTY до запуска <c>claude</c>.</summary>
    public string PreLaunch
    {
        get => _preLaunch;
        set => SetProperty(ref _preLaunch, value ?? string.Empty);
    }

    /// <summary>Дополнительные аргументы одной строкой; разбор — в <see cref="ExtraArgsSyntax"/>.</summary>
    public string ExtraArgsText
    {
        get => _extraArgsText;
        set => SetProperty(ref _extraArgsText, value ?? string.Empty);
    }

    /// <summary>
    /// Поля заполнены достаточно, чтобы сохранять. Существование каталога сюда не входит
    /// намеренно: по разделу 8 ТЗ исчезнувший каталог блокирует запуск сессии, а не правку
    /// настроек — каталог может появиться позже. По той же причине сохранению не мешает
    /// и неустановленная оболочка: её можно поставить потом, а до тех пор запуск откатится
    /// на доступную, о чём говорит <see cref="ShellWarning"/>.
    /// </summary>
    public bool IsValid => Name.Trim().Length > 0 && ProjectPath.Trim().Length > 0;

    /// <summary>Чего не хватает для сохранения. Пусто, когда всё заполнено.</summary>
    public string ValidationMessage
    {
        get
        {
            if (ProjectPath.Trim().Length == 0)
            {
                return "Укажите каталог проекта.";
            }

            return Name.Trim().Length == 0 ? "Укажите отображаемое имя проекта." : string.Empty;
        }
    }

    /// <summary>Каталог проверен и не найден.</summary>
    public bool IsDirectoryMissing => _directoryProbeResult == ProbeResult.Missing;

    /// <summary>Предупреждение о ненайденном каталоге. Пусто, когда предупреждать не о чем.</summary>
    public string DirectoryWarning =>
        IsDirectoryMissing
            ? "Каталог не найден. Сохранить настройки можно — он может появиться позже, но запустить сессию не получится."
            : string.Empty;

    /// <summary>Есть что сказать про выбранную оболочку.</summary>
    public bool HasShellWarning => ShellWarning.Length > 0;

    /// <summary>
    /// Что произойдёт при запуске, если выбранной оболочки в системе нет (раздел 8 ТЗ).
    /// Пусто, пока проверка не ответила и пока выбранная оболочка установлена.
    /// </summary>
    public string ShellWarning
    {
        get
        {
            if (!_shellsProbed)
            {
                return string.Empty;
            }

            if (_installedShells is not { } installed)
            {
                return "Проверить, какие оболочки установлены, не удалось. При запуске будет выбрана доступная.";
            }

            if (installed.Contains(_selectedShell))
            {
                return string.Empty;
            }

            if (installed.Count == 0)
            {
                return "В системе не найдено ни одной оболочки: запустить сессию не получится.";
            }

            // Первый элемент списка и есть замена: порядок в нём — порядок провайдеров,
            // по которому резолвер откатывается с отсутствующей оболочки.
            return $"«{ShellOptions.TitleFor(_selectedShell)}» не установлена. "
                + $"Сессии этого проекта запустятся в «{ShellOptions.TitleFor(installed[0])}».";
        }
    }

    /// <summary>Справка о <c>CLAUDE.md</c> в каталоге проекта.</summary>
    public string ClaudeMdHint => DescribeFile(ClaudeMdFileName, _claudeMdProbeResult);

    /// <summary>Справка о <c>.mcp.json</c> в каталоге проекта.</summary>
    public string McpJsonHint => DescribeFile(McpJsonFileName, _mcpJsonProbeResult);

    /// <summary>
    /// Перепроверяет каталог и справочные файлы. Зовётся на открытии диалога, после выбора
    /// папки и когда поле пути теряет фокус — периодического опроса здесь нет (раздел 7 CLAUDE.md).
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var path = ProjectPath.Trim();
        var generation = ++_probeGeneration;

        if (path.Length == 0)
        {
            ApplyProbeResults(generation, ProbeResult.Unchecked, ProbeResult.Unchecked, ProbeResult.Unchecked);
            return;
        }

        try
        {
            var directoryExists = await _directoryProbe.ExistsAsync(path, cancellationToken).ConfigureAwait(true);
            if (!directoryExists)
            {
                // Про файлы в несуществующем каталоге сказать нечего, и «не найден» тут было бы шумом.
                ApplyProbeResults(generation, ProbeResult.Missing, ProbeResult.Unchecked, ProbeResult.Unchecked);
                return;
            }

            var claudeMd = await ProbeFileAsync(path, ClaudeMdFileName, cancellationToken).ConfigureAwait(true);
            var mcpJson = await ProbeFileAsync(path, McpJsonFileName, cancellationToken).ConfigureAwait(true);
            ApplyProbeResults(generation, ProbeResult.Found, claudeMd, mcpJson);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // Справка о каталоге не стоит того, чтобы ронять окно (раздел 8 ТЗ): недоступный
            // путь остаётся непроверенным, а сохранить настройки пользователю никто не мешает.
            ApplyProbeResults(generation, ProbeResult.Unchecked, ProbeResult.Unchecked, ProbeResult.Unchecked);
        }
    }

    /// <summary>
    /// Узнаёт, какие оболочки установлены, и помечает в списке ненайденные. Зовётся один раз,
    /// уже после показа окна: поиск по <c>PATH</c> не должен задерживать открытие диалога.
    /// </summary>
    public async Task RefreshShellsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _installedShells = await _shellAvailability.GetInstalledAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // Диск ответил отказом: пометок не будет, а предупреждение честно скажет, что
            // проверить не удалось. Ронять окно из-за справки нельзя (раздел 8 ТЗ).
            _installedShells = null;
        }

        _shellsProbed = true;
        _shells = ShellOptions.Build(_selectedShell, _installedShells);

        // Сначала список, потом выбранный элемент: иначе выпадающий список на мгновение
        // остаётся с элементом, которого в нём уже нет.
        Raise(nameof(Shells));
        Raise(nameof(Shell));
        RaiseShellWarning();
    }

    /// <summary>
    /// Спрашивает у пользователя каталог и подставляет его. Имя подставляется по папке,
    /// только если пользователь его не задавал сам, — иначе выбор папки затирал бы правку.
    /// </summary>
    public async Task BrowseAsync(CancellationToken cancellationToken)
    {
        var picked = _folderPicker.PickFolder("Каталог проекта");
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        var previousDefault = DefaultNameOrNull(ProjectPath);
        var currentName = Name.Trim();

        ProjectPath = picked;

        if (currentName.Length == 0
            || (previousDefault is not null && string.Equals(currentName, previousDefault, StringComparison.Ordinal)))
        {
            Name = DefaultNameOrNull(picked) ?? currentName;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Собирает результат и просит закрыть диалог. Незаполненные поля игнорируются.</summary>
    public void Save()
    {
        if (!IsValid)
        {
            return;
        }

        var preLaunch = PreLaunch.Trim();

        Result = _original with
        {
            Name = Name.Trim(),
            Path = ProjectPath.Trim(),
            Shell = _selectedShell,
            PreLaunch = preLaunch.Length == 0 ? null : preLaunch,
            ExtraArgs = ExtraArgsSyntax.Parse(ExtraArgsText),
        };

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string? DefaultNameOrNull(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : ProjectNaming.DefaultNameFor(path);

    private static string DescribeFile(string fileName, ProbeResult result) => result switch
    {
        ProbeResult.Found => $"{fileName} — найден",
        ProbeResult.Missing => $"{fileName} — не найден",
        _ => $"{fileName} — не проверялся",
    };

    private async Task<ProbeResult> ProbeFileAsync(
        string directory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var full = System.IO.Path.Combine(directory, fileName);
        var exists = await _fileProbe.ExistsAsync(full, cancellationToken).ConfigureAwait(true);
        return exists ? ProbeResult.Found : ProbeResult.Missing;
    }

    private void ApplyProbeResults(int generation, ProbeResult directory, ProbeResult claudeMd, ProbeResult mcpJson)
    {
        if (generation != _probeGeneration)
        {
            return;
        }

        _directoryProbeResult = directory;
        _claudeMdProbeResult = claudeMd;
        _mcpJsonProbeResult = mcpJson;
        RaiseProbeProperties();
    }

    private void ResetProbeResults()
    {
        _directoryProbeResult = ProbeResult.Unchecked;
        _claudeMdProbeResult = ProbeResult.Unchecked;
        _mcpJsonProbeResult = ProbeResult.Unchecked;

        // Гонку тоже снимаем: ответ уже запущенной проверки относится к прежнему пути.
        _probeGeneration++;
        RaiseProbeProperties();
    }

    private void RaiseProbeProperties()
    {
        Raise(nameof(IsDirectoryMissing));
        Raise(nameof(DirectoryWarning));
        Raise(nameof(ClaudeMdHint));
        Raise(nameof(McpJsonHint));
    }

    private void RaiseShellWarning()
    {
        Raise(nameof(ShellWarning));
        Raise(nameof(HasShellWarning));
    }

    private void RaiseValidity()
    {
        Raise(nameof(IsValid));
        Raise(nameof(ValidationMessage));

        // Доступность «сохранить» меняется по мере набора текста. Жест пользователя запрос
        // доступности вызывает, но выбор папки меняет оба поля уже после клика — без явного
        // запроса кнопка осталась бы выключенной до следующего нажатия клавиши.
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Итог проверки существования на диске.</summary>
    private enum ProbeResult
    {
        /// <summary>Проверка ещё не выполнялась либо её результат устарел.</summary>
        Unchecked = 0,

        /// <summary>Найден.</summary>
        Found = 1,

        /// <summary>Проверен и не найден.</summary>
        Missing = 2,
    }
}
