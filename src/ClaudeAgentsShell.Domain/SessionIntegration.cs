namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Файлы интеграции с приложением, которые передаются конкретному запуску <c>claude</c>.
/// Оба лежат в каталоге данных приложения — в проект пользователя не пишется ничего.
/// </summary>
/// <param name="HookSettingsPath">
/// Файл настроек с хуками для <c>--settings</c>; <c>null</c> — запуск без хуков
/// (раздел 5.3 ТЗ допускает такую деградацию).
/// </param>
/// <param name="McpConfigPath">
/// Файл для <c>--mcp-config</c> с инструментом <c>show_diff</c>; <c>null</c> — приёмник
/// не поднялся, и сервер не подключается вовсе, чтобы вкладка не показывала сбой соединения.
/// </param>
/// <remarks>
/// Параметр именно запуска: одна вкладка может получить файлы, а соседняя — нет, если сбой
/// случился между их открытием. Файл MCP может оказаться своим на каждую вкладку (issue #5),
/// и сигнатура построителя команды от этого не меняется.
/// </remarks>
public sealed record SessionIntegration(string? HookSettingsPath, string? McpConfigPath)
{
    /// <summary>Запуск без интеграции.</summary>
    public static SessionIntegration None { get; } = new(null, null);
}
