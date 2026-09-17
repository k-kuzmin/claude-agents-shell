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
    /// Имя переменной окружения, через которую вкладка передаёт свой токен в хуки.
    /// Значение подставляется в окружение псевдоконсоли при запуске сессии.
    /// </summary>
    string TokenVariableName { get; }

    /// <summary>
    /// Создаёт (или обновляет) файл настроек под текущий адрес приёмника и возвращает путь к нему.
    /// Путь передаётся <c>claude</c> аргументом <c>--settings</c>.
    /// </summary>
    /// <exception cref="IOException">Файл создать не удалось; сессия запускается без хуков.</exception>
    Task<string> EnsureSettingsFileAsync(Uri endpoint, CancellationToken cancellationToken);
}
