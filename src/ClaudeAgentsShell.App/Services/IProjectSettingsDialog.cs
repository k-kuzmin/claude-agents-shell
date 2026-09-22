using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Зачем открыт диалог. От этого зависят только подписи окна и главной кнопки.</summary>
public enum ProjectSettingsPurpose
{
    /// <summary>Правка уже существующего проекта.</summary>
    Edit = 0,

    /// <summary>Добавление нового проекта: настройки задаются до записи в <c>projects.json</c>.</summary>
    Add = 1,
}

/// <summary>
/// Диалог добавления и настроек проекта (раздел 6.5 ТЗ): путь с выбором папки, отображаемое
/// имя, оболочка, команда перед запуском, дополнительные аргументы.
/// </summary>
/// <remarks>
/// Порт уровня оболочки: модальное окно — WPF-специфика, и во ViewModel ей места нет.
/// Диалог ничего не сохраняет сам: он только возвращает отредактированное описание, а решение
/// записать его принимает прикладной код.
/// <para>
/// Наличие <c>CLAUDE.md</c> и <c>.mcp.json</c> диалог показывает справочно, через
/// <see cref="Application.Ports.IFileProbe"/>. Приложение эти файлы не читает и не меняет —
/// это запрет раздела 7 CLAUDE.md.
/// </para>
/// </remarks>
public interface IProjectSettingsDialog
{
    /// <summary>
    /// Показывает диалог поверх главного окна и ждёт его закрытия.
    /// </summary>
    /// <param name="project">
    /// Что редактируем либо заготовка нового проекта. Идентификатор и порядок диалог не меняет.
    /// </param>
    /// <param name="purpose">Добавление или правка: меняет только подписи.</param>
    /// <param name="cancellationToken">Отмена ожидания.</param>
    /// <returns>
    /// Отредактированное описание проекта либо <c>null</c>, если пользователь отказался.
    /// <c>null</c> — штатный исход, а не ошибка.
    /// </returns>
    Task<ProjectDefinition?> ShowAsync(
        ProjectDefinition project,
        ProjectSettingsPurpose purpose,
        CancellationToken cancellationToken);
}
