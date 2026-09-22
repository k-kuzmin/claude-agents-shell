namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Раскладка окна, которую приложение поднимает при следующем запуске (<c>layout.json</c>,
/// раздел 4.2 ТЗ). В отличие от ТЗ, вкладки восстанавливаются автоматически — отступление
/// согласовано с пользователем 22.09.2026, issue #4.
/// </summary>
/// <param name="ActiveProjectId">Выбранный проект; <c>null</c>, если не был выбран ни один.</param>
/// <param name="Projects">Проекты с открытыми вкладками, в порядке панели проектов.</param>
public sealed record WorkspaceLayout(Guid? ActiveProjectId, IReadOnlyList<ProjectLayout> Projects)
{
    /// <summary>Пустая раскладка: нечего восстанавливать.</summary>
    public static WorkspaceLayout Empty { get; } = new(null, []);
}

/// <summary>Вкладки одного проекта.</summary>
/// <param name="ProjectId">Проект из <c>projects.json</c>.</param>
/// <param name="ActiveTabIndex">Индекс активной вкладки проекта в <paramref name="Tabs"/>; вне диапазона — первая.</param>
/// <param name="Tabs">Вкладки в порядке полосы: порядок массива и есть порядок вкладок.</param>
public sealed record ProjectLayout(Guid ProjectId, int ActiveTabIndex, IReadOnlyList<TabLayout> Tabs);

/// <summary>
/// Одна вкладка. В раскладку попадают только живые вкладки: оболочка работает и последним
/// событием жизненного цикла сессии был не <c>SessionEnd</c> (решение пользователя 22.09.2026).
/// </summary>
/// <param name="SessionId">
/// Сессия Claude Code для <c>--resume</c>; <c>null</c> — хуки не сработали, вкладка
/// поднимается новой сессией.
/// </param>
/// <param name="ShortTitle">Короткое имя, показанное до прихода заголовка из транскрипта.</param>
public sealed record TabLayout(string? SessionId, string? ShortTitle);
