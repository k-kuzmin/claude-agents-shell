namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Вкладка глазами раскладки: ровно то, что нужно для снимка, без ViewModel.
/// </summary>
/// <param name="ProjectId">Проект вкладки.</param>
/// <param name="SessionId">Сессия Claude Code; <c>null</c>, пока неизвестна.</param>
/// <param name="ShortTitle">Короткое имя; <c>null</c> — «новая сессия».</param>
/// <param name="IsLive">Оболочка работает и сессия не закончилась — только такие идут в раскладку.</param>
/// <param name="IsActiveInProject">Эта вкладка — активная вкладка своего проекта.</param>
public sealed record LayoutTabSnapshot(
    Guid ProjectId,
    string? SessionId,
    string? ShortTitle,
    bool IsLive,
    bool IsActiveInProject);
