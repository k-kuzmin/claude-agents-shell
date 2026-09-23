using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Что поднимать при старте: проекты с доступным каталогом в сохранённом порядке
/// и сохранённый активный проект, если он ещё есть в списке.
/// </summary>
/// <param name="Projects">Проекты, чьи вкладки открываются, в сохранённом порядке.</param>
/// <param name="ActiveProjectId">Сохранённый активный проект; <c>null</c> — его нет или он убран из списка.</param>
public sealed record LayoutRestorePlan(IReadOnlyList<ProjectLayout> Projects, Guid? ActiveProjectId)
{
    /// <summary>Режим запуска вкладки: есть сессия — <c>--resume</c>, нет — новая сессия.</summary>
    public static SessionLaunch LaunchFor(TabLayout tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.SessionId is { } sessionId
            ? new SessionLaunch.ResumeSession(sessionId)
            : new SessionLaunch.NewSession();
    }
}
