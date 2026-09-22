namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Готовит файл настроек с хуками, который передаётся сессии через <c>--settings</c>.
/// </summary>
/// <remarks>
/// Файл генерируется **в каталоге данных приложения**. В проект пользователя и в
/// <c>~/.claude</c> не пишется ничего — это запрет раздела 7 CLAUDE.md.
/// Вкладка опознаётся не отдельным файлом на сессию, а токеном из окружения псевдоконсоли:
/// файл один на приложение, а команда хука подставляет токен сама.
/// </remarks>
public interface IHookSettingsProvider
{
    /// <summary>
    /// Переменные, которые добавляются к окружению псевдоконсоли сессии поверх унаследованного:
    /// токен вкладки, по которому хуки опознают свою вкладку, и всё, что нужно транспорту хуков,
    /// чтобы дойти до приёмника (например, обход прокси для loopback).
    /// </summary>
    /// <param name="token">Токен вкладки, выданный при её открытии.</param>
    IReadOnlyDictionary<string, string> SessionEnvironment(string token);

    /// <summary>
    /// Создаёт (или обновляет) файл настроек под текущий адрес приёмника и возвращает путь к нему.
    /// Путь передаётся <c>claude</c> аргументом <c>--settings</c>.
    /// </summary>
    /// <exception cref="IOException">Файл создать не удалось; сессия запускается без хуков.</exception>
    Task<string> EnsureSettingsFileAsync(Uri endpoint, CancellationToken cancellationToken);
}
