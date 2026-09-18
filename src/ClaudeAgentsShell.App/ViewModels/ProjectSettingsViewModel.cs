using System.Windows.Input;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Диалог настроек проекта (раздел 6.5 ТЗ): путь, отображаемое имя, оболочка, команда перед
/// запуском и дополнительные аргументы. Ничего не сохраняет — только собирает
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
    private readonly IFolderPicker _folderPicker;
    private readonly IDirectoryProbe _directoryProbe;
    private readonly IFileProbe _fileProbe;

    private string _projectPath;
    private string _name;
    private ShellOption _shell;
    private string _preLaunch;
    private string _extraArgsText;

    private ProbeResult _directoryProbeResult = ProbeResult.Unchecked;
    private ProbeResult _claudeMdProbeResult = ProbeResult.Unchecked;
    private ProbeResult _mcpJsonProbeResult = ProbeResult.Unchecked;

    // Поколение проверки: пользователь правит путь быстрее, чем отвечает сетевая шара,
    // и результат устаревшей проверки не должен перебить более свежую.
    private int _probeGeneration;

    /// <inheritdoc cref="ProjectSettingsViewModel" />
    /// <param name="project">Что редактируем.</param>
    /// <param name="folderPicker">Выбор каталога.</param>
    /// <param name="directoryProbe">Проверка существования каталога.</param>
    /// <param name="fileProbe">Проверка наличия <c>CLAUDE.md</c> и <c>.mcp.json</c>.</param>
    public ProjectSettingsViewModel(
        ProjectDefinition project,
        IFolderPicker folderPicker,
        IDirectoryProbe directoryProbe,
        IFileProbe fileProbe)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(directoryProbe);
        ArgumentNullException.ThrowIfNull(fileProbe);

        _original = project;
        _folderPicker = folderPicker;
        _directoryProbe = directoryProbe;
        _fileProbe = fileProbe;

        _projectPath = project.Path ?? string.Empty;
        _name = project.Name ?? string.Empty;
        _shell = ShellOptions.For(project.Shell);
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

    /// <summary>Оболочки, доступные для выбора.</summary>
    public IReadOnlyList<ShellOption> Shells => ShellOptions.All;

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
    public ShellOption Shell
    {
        get => _shell;
        set => SetProperty(ref _shell, value ?? ShellOptions.For(ShellKind.Pwsh));
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
    /// настроек — каталог может появиться позже.
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
            Shell = Shell.Kind,
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
