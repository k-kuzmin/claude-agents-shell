using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Раскладка окна для корневой ViewModel (issue #4): чтение и план восстановления, снимок
/// из вкладок и запись по изменениям через <see cref="LayoutRecorder" />. ViewModel открывает
/// вкладки и выставляет активные, а решает, что сохранять и что поднимать, этот класс.
/// <para>
/// Проект, который есть в списке, но чей каталог недоступен на старте, не поднимается, а его
/// записи переносятся в каждый снимок как есть: пока в проекте не откроют живую вкладку (тогда
/// записи заменяются живыми, см. <see cref="ForgetDeferred" />) или пока проект не уберут из
/// списка (тогда снимок его больше не видит).
/// </para>
/// </summary>
/// <remarks>Все члены, кроме чтения, вызываются в потоке интерфейса.</remarks>
public sealed class WorkspaceLayoutService
{
    private readonly ILayoutStore _store;
    private readonly LayoutRecorder _recorder;
    private readonly Dictionary<Guid, ProjectLayout> _deferred = [];

    /// <param name="store">Хранилище раскладки.</param>
    /// <param name="recorder">Запись раскладки по изменениям.</param>
    public WorkspaceLayoutService(ILayoutStore store, LayoutRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recorder);

        _store = store;
        _recorder = recorder;
    }

    /// <summary>
    /// Читает сохранённую раскладку и строит план восстановления. Проекта больше нет в списке —
    /// его вкладки отбрасываются; каталог недоступен — вкладки откладываются для переноса.
    /// </summary>
    /// <param name="isKnown">Есть ли проект в списке.</param>
    /// <param name="isAvailable">Доступен ли каталог проекта (зовётся только для известных).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<LayoutRestorePlan> PlanRestoreAsync(
        Func<Guid, bool> isKnown,
        Func<Guid, CancellationToken, Task<bool>> isAvailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isKnown);
        ArgumentNullException.ThrowIfNull(isAvailable);

        var layout = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        var projects = new List<ProjectLayout>(layout.Projects.Count);

        foreach (var project in layout.Projects)
        {
            if (!isKnown(project.ProjectId))
            {
                continue;
            }

            if (!await isAvailable(project.ProjectId, cancellationToken).ConfigureAwait(true))
            {
                _deferred[project.ProjectId] = project;
                continue;
            }

            projects.Add(project);
        }

        var active = layout.ActiveProjectId is { } id && isKnown(id) ? id : (Guid?)null;
        return new LayoutRestorePlan(projects, active);
    }

    /// <summary>
    /// Снимок раскладки. Живость берётся из <paramref name="tabs" /> как есть: снимок снимается
    /// в момент записи. У проекта без живых вкладок переносятся его отложенные записи.
    /// </summary>
    /// <param name="projectOrder">Проекты списка в порядке панели.</param>
    /// <param name="tabs">Все вкладки в порядке полосы.</param>
    /// <param name="activeProjectId">Выбранный проект.</param>
    public WorkspaceLayout Capture(
        IEnumerable<Guid> projectOrder,
        IReadOnlyList<LayoutTabSnapshot> tabs,
        Guid? activeProjectId)
    {
        ArgumentNullException.ThrowIfNull(projectOrder);
        ArgumentNullException.ThrowIfNull(tabs);

        var projects = new List<ProjectLayout>();

        foreach (var projectId in projectOrder)
        {
            var live = tabs.Where(tab => tab.ProjectId == projectId && tab.IsLive).ToList();
            if (live.Count == 0)
            {
                if (_deferred.TryGetValue(projectId, out var deferred))
                {
                    projects.Add(deferred);
                }

                continue;
            }

            // Активная вкладка умерла или не отмечена — активной считается первая живая.
            var activeIndex = Math.Max(live.FindIndex(static tab => tab.IsActiveInProject), 0);

            projects.Add(new ProjectLayout(
                projectId,
                activeIndex,
                [.. live.Select(static tab => new TabLayout(tab.SessionId, tab.ShortTitle))]));
        }

        return new WorkspaceLayout(activeProjectId, projects);
    }

    /// <summary>
    /// Снимает отложенные записи проекта: в нём открыли живую вкладку (иначе после возврата
    /// каталога поднялись бы и прежние вкладки, и новые — дублями) или проект убрали из списка.
    /// </summary>
    public void ForgetDeferred(Guid projectId) => _deferred.Remove(projectId);

    /// <summary>Включает запись раскладки — после того, как восстановление закончено.</summary>
    public void StartRecording(Func<WorkspaceLayout> capture) => _recorder.Start(capture);

    /// <summary>Раскладка изменилась.</summary>
    public void Signal() => _recorder.Signal();

    /// <inheritdoc cref="LayoutRecorder.FlushAndFreezeAsync" />
    public Task FlushAndFreezeAsync(CancellationToken cancellationToken) =>
        _recorder.FlushAndFreezeAsync(cancellationToken);

    /// <inheritdoc cref="LayoutRecorder.Freeze" />
    public void Freeze() => _recorder.Freeze();
}
