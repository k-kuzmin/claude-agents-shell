using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Готовит файл настроек с хуками и сопровождающий его командный файл — оба в каталоге данных
/// приложения. В проект пользователя и в <c>~/.claude</c> не пишется ничего (раздел 7 CLAUDE.md).
/// <para>
/// Все хуки, кроме <c>SessionStart</c>, — HTTP-хуки Claude Code: тело запроса то же, что
/// command-хук получил бы на stdin, токен вкладки подставляется в заголовок из переменной
/// окружения псевдоконсоли. Процесса на хук нет вовсе — это и позволяет зарегистрировать
/// частый <c>PostToolBatch</c>.
/// </para>
/// <para>
/// <c>SessionStart</c> HTTP не поддерживает и остаётся command-хуком: полезную нагрузку
/// отправляет <c>curl.exe</c>, который есть в Windows 10 и 11 из коробки. Команда завёрнута
/// в <c>.cmd</c>, потому что хуки запускаются через <c>cmd.exe</c>: там разворачивается
/// <c>%ПЕРЕМЕННАЯ%</c> с токеном вкладки и там же гарантируется тихий выход.
/// </para>
/// </summary>
public sealed class HookSettingsProvider : IHookSettingsProvider
{
    /// <summary>Имя генерируемого файла настроек; передаётся <c>claude</c> через <c>--settings</c>.</summary>
    public const string SettingsFileName = "hook-settings.json";

    /// <summary>Имя командного файла, который вызывается из каждого хука.</summary>
    public const string ScriptFileName = "hook-send.cmd";

    private const string TempSuffix = ".tmp";

    private const string NoProxyVariableName = "NO_PROXY";
    private const string LoopbackHosts = "127.0.0.1,localhost";

    // Command-хук ждёт запуска cmd.exe и curl.exe, HTTP-хук — только ответа приёмника.
    private const int CommandTimeoutSeconds = 5;
    private const int HttpTimeoutSeconds = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppDataPaths _paths;

    /// <inheritdoc cref="HookSettingsProvider" />
    public HookSettingsProvider(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Имя переменной окружения псевдоконсоли, через которую вкладка передаёт токен в хуки.</summary>
    public string TokenVariableName => HookProtocol.TokenVariableName;

    /// <inheritdoc />
    /// <remarks>
    /// Кроме токена — <c>NO_PROXY</c> с дописанным loopback: HTTP-хуки Claude Code иначе
    /// уйдут в прокси из окружения и не доедут до приёмника на 127.0.0.1. Существующее
    /// значение сохраняется. Ключ один: окружение Windows регистр имён не различает.
    /// </remarks>
    public IReadOnlyDictionary<string, string> SessionEnvironment(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HookProtocol.TokenVariableName] = token,
            [NoProxyVariableName] = LoopbackBypassingProxy(
                Environment.GetEnvironmentVariable(NoProxyVariableName)
                ?? Environment.GetEnvironmentVariable("no_proxy")),
        };
    }

    /// <summary>
    /// Значение <c>NO_PROXY</c> для сессии: существующее с дописанным <c>127.0.0.1,localhost</c>,
    /// либо только loopback, если своего значения нет.
    /// </summary>
    /// <param name="existing">Значение <c>NO_PROXY</c> процесса приложения, если есть.</param>
    public static string LoopbackBypassingProxy(string? existing) =>
        string.IsNullOrWhiteSpace(existing) ? LoopbackHosts : $"{existing.TrimEnd(',')},{LoopbackHosts}";

    /// <inheritdoc />
    public async Task<string> EnsureSettingsFileAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Обращение к AppData создаёт каталог данных, если его ещё нет.
        var directory = _paths.AppData;
        var script = Path.Combine(directory, ScriptFileName);
        var settings = Path.Combine(directory, SettingsFileName);

        // Оба файла пишутся через временный: сессия могла открыть прежний по --settings,
        // и надорванный файл хуже устаревшего.
        await WriteAtomicAsync(script, BuildScript(endpoint), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(settings, BuildSettings(script, endpoint), cancellationToken).ConfigureAwait(false);

        return settings;
    }

    /// <summary>
    /// Командный файл: читает полезную нагрузку хука со stdin и отправляет её приёмнику.
    /// Ничего не печатает и всегда завершается нулём — иначе <c>Stop</c> не дал бы агенту
    /// остановиться, а вывод <c>SessionStart</c> уехал бы в контекст модели.
    /// </summary>
    private static string BuildScript(Uri endpoint)
    {
        var builder = new StringBuilder();
        builder.Append("@echo off\r\n");
        builder.Append("rem Generated by Claude Agents Shell. Sends the hook payload to the running app.\r\n");
        builder.Append("rem Rewritten on every start: the listener port changes.\r\n");
        // --noproxy обязателен: HTTP_PROXY в окружении пользователя увёл бы вызов к самому себе
        // через прокси, и маркеры состояния пропали бы молча.
        builder.Append("curl.exe -s -o nul --max-time 3 --noproxy \"*\"");
        builder.Append(" -H \"Content-Type: application/json\"");
        builder.Append($" -H \"{HookProtocol.TokenHeaderName}: %{HookProtocol.TokenVariableName}%\"");
        builder.Append(" --data-binary @- \"");
        builder.Append(endpoint.AbsoluteUri.TrimEnd('/'));
        builder.Append("\" 2>nul\r\n");
        builder.Append("exit /b 0\r\n");
        return builder.ToString();
    }

    private static string BuildSettings(string scriptPath, Uri endpoint)
    {
        // Путь берётся в кавычки всегда: cmd понимает такую команду и с пробелами в пути, и без них.
        var command = new HookMatcherDto
        {
            Hooks =
            [
                new HookHandlerDto
                {
                    Type = "command",
                    Command = $"\"{scriptPath}\"",
                    Timeout = CommandTimeoutSeconds,
                },
            ],
        };

        // Значение заголовка — ссылка на переменную, а не сам токен: файл настроек общий
        // для всех вкладок, токен у каждой свой. Claude Code подставляет только переменные,
        // перечисленные в allowedEnvVars.
        var http = new HookMatcherDto
        {
            Hooks =
            [
                new HookHandlerDto
                {
                    Type = "http",
                    Url = endpoint.AbsoluteUri.TrimEnd('/'),
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [HookProtocol.TokenHeaderName] = "$" + HookProtocol.TokenVariableName,
                    },
                    AllowedEnvVars = [HookProtocol.TokenVariableName],
                    Timeout = HttpTimeoutSeconds,
                },
            ],
        };

        var document = new HookSettingsDto
        {
            // Состояние вкладки выводится только из этих хуков (раздел 7 CLAUDE.md). Имена точные:
            // незнакомое имя Claude Code молча пропустит, и вкладка останется без маркера.
            // PreToolUse не регистрируется сознательно: при недоступном HTTP-приёмнике он
            // отказывает инструменту, остальные хуки деградируют до предупреждения.
            // PostToolBatch — широкий вход в «работает» (issue #1), PermissionRequest —
            // мгновенный сигнал ожидания человека; оба без matcher, то есть на любой инструмент.
            // Матчер у SessionStart не сужается до отдельных source: сжатие контекста приходит
            // тем же хуком, и решение «это не граница хода» принимает координатор. Получить
            // событие и осознанно ничего не сделать надёжнее, чем его не увидеть.
            Hooks = new Dictionary<string, List<HookMatcherDto>>(StringComparer.Ordinal)
            {
                ["SessionStart"] = [command],
                ["UserPromptSubmit"] = [http],
                ["Stop"] = [http],
                ["StopFailure"] = [http],
                ["SubagentStart"] = [http],
                ["SubagentStop"] = [http],
                ["SessionEnd"] = [http],
                ["PostToolBatch"] = [http],
                ["PermissionRequest"] = [http],
            },
        };

        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + TempSuffix;
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class HookSettingsDto
    {
        [JsonPropertyName("hooks")]
        public Dictionary<string, List<HookMatcherDto>> Hooks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class HookMatcherDto
    {
        [JsonPropertyName("hooks")]
        public List<HookHandlerDto> Hooks { get; set; } = [];
    }

    /// <summary>
    /// Обработчик хука. Поля, которых у типа нет, остаются <c>null</c> и в файл не пишутся:
    /// у HTTP-хука нет <c>command</c>, у command-хука — <c>url</c>, <c>headers</c> и
    /// <c>allowedEnvVars</c>.
    /// </summary>
    private sealed class HookHandlerDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("allowedEnvVars")]
        public List<string>? AllowedEnvVars { get; set; }

        [JsonPropertyName("timeout")]
        public int Timeout { get; set; }
    }
}
