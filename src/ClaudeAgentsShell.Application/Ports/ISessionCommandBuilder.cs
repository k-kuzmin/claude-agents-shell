using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Собирает строки, которые пишутся в stdin поднятой оболочки, чтобы запустить сессию.
/// Чистая функция без ввода-вывода — поэтому все три режима запуска и экранирование
/// аргументов покрываются модульными тестами без живого PTY.
/// </summary>
public interface ISessionCommandBuilder
{
    /// <summary>
    /// Строки для записи в stdin по порядку. Каждая завершена символом <c>\r</c>: LF оболочка
    /// за нажатие Enter не считает, и команда осталась бы в буфере невыполненной.
    /// Первой идёт <see cref="ProjectDefinition.PreLaunch"/>, если задана, затем команда
    /// запуска <c>claude</c> с учётом режима и <see cref="ProjectDefinition.ExtraArgs"/>.
    /// </summary>
    /// <param name="project">Проект, в каталоге которого поднята оболочка.</param>
    /// <param name="launch">Режим запуска.</param>
    /// <param name="hookSettingsPath">
    /// Путь к сгенерированному файлу настроек с хуками — уходит в <c>--settings</c>.
    /// <c>null</c> означает запуск без хуков: файл создать не удалось, и сессия работает
    /// без маркера состояния (раздел 5.3 ТЗ допускает такую деградацию).
    /// Параметр именно запуска, а не настройка построителя: одна вкладка может получить файл,
    /// а соседняя — нет, если сбой случился между их открытием.
    /// </param>
    IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch, string? hookSettingsPath);
}
