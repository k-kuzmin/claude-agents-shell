using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Hooks;

namespace ClaudeAgentsShell.Sessions.Mcp;

/// <summary>
/// Пишет <c>mcp.json</c> для <c>--mcp-config</c> в каталог данных приложения: один HTTP-сервер
/// <c>agents-shell</c> на MCP-маршруте приёмника хуков.
/// <para>
/// Файл один на все вкладки. Токен вкладки в нём — ссылка <c>${ПЕРЕМЕННАЯ}</c>, которую
/// Claude Code разворачивает из окружения псевдоконсоли. Проверено вживую на claude 2.1.280
/// (issue #5): подстановка в <c>headers</c> работает и в файле <c>--mcp-config</c>, и с именем
/// <see cref="HookProtocol.TokenVariableName"/>. Синтаксис не тот, что у HTTP-хуков: там
/// <c>$ИМЯ</c> и <c>allowedEnvVars</c>, здесь фигурные скобки и без списка разрешённых.
/// </para>
/// </summary>
public sealed class McpConfigProvider : IMcpConfigProvider
{
    /// <summary>Имя генерируемого файла.</summary>
    public const string ConfigFileName = "mcp.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly IAppDataPaths _paths;

    /// <inheritdoc cref="McpConfigProvider" />
    public McpConfigProvider(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<string> EnsureConfigFileAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Обращение к AppData создаёт каталог данных, если его ещё нет.
        var path = Path.Combine(_paths.AppData, ConfigFileName);
        await AtomicTextFile.WriteAsync(path, BuildConfig(endpoint), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Содержимое файла под адрес <paramref name="endpoint"/>.</summary>
    private static string BuildConfig(Uri endpoint)
    {
        var server = new ServerDto
        {
            Url = endpoint.AbsoluteUri.TrimEnd('/'),
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HookProtocol.TokenHeaderName] = "${" + HookProtocol.TokenVariableName + "}",
            },
        };

        var document = new ConfigDto
        {
            McpServers = new Dictionary<string, ServerDto>(StringComparer.Ordinal)
            {
                [McpProtocol.ServerName] = server,
            },
        };

        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private sealed class ConfigDto
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, ServerDto> McpServers { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ServerDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "http";

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("headers")]
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);
    }
}
