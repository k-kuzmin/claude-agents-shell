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
    /// Строки для записи в stdin по порядку. Каждая уже завершена переводом строки.
    /// Первой идёт <see cref="ProjectDefinition.PreLaunch"/>, если задана, затем команда
    /// запуска <c>claude</c> с учётом режима и <see cref="ProjectDefinition.ExtraArgs"/>.
    /// </summary>
    IReadOnlyList<string> Build(ProjectDefinition project, SessionLaunch launch);
}
