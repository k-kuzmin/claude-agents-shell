namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Проект — рабочий каталог, в котором запускаются сессии. Хранится в <c>projects.json</c>.
/// </summary>
/// <param name="Id">Стабильный идентификатор строки списка.</param>
/// <param name="Name">Отображаемое имя.</param>
/// <param name="Path">Рабочий каталог сессий.</param>
/// <param name="Shell">Предпочитаемая оболочка; фактическая определяется резолвером.</param>
/// <param name="PreLaunch">Команда, выполняемая в PTY до запуска <c>claude</c>. Пусто — ничего не выполняется.</param>
/// <param name="ExtraArgs">Дополнительные аргументы командной строки <c>claude</c> для этого проекта.</param>
/// <param name="Order">Порядок в списке проектов.</param>
public sealed record ProjectDefinition(
    Guid Id,
    string Name,
    string Path,
    ShellKind Shell,
    string? PreLaunch,
    IReadOnlyList<string> ExtraArgs,
    int Order);
