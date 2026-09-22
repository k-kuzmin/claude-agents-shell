namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Запрос, по которому построена панель вкладки: его повторяют «обновить», смена базы,
/// <c>-w</c> и рабочего дерева.
/// </summary>
/// <param name="Directory">Каталог, от которого считается diff.</param>
/// <param name="BaseRef">База; <c>null</c> — выбирает читатель git.</param>
/// <param name="IgnoreWhitespace">Без учёта пробелов.</param>
/// <param name="Files">Файлы, указанные агентом; пусто — все.</param>
/// <param name="Note">Пояснение агента над оглавлением.</param>
internal sealed record DiffQuery(
    string Directory,
    string? BaseRef,
    bool IgnoreWhitespace,
    IReadOnlyList<string> Files,
    string? Note);
