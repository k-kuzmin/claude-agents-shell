namespace ClaudeAgentsShell.Domain;

/// <summary>Вид изменения файла в diff.</summary>
public enum DiffChangeKind
{
    /// <summary>Файл изменён.</summary>
    Modified,

    /// <summary>Файл добавлен и отслеживается.</summary>
    Added,

    /// <summary>Файл удалён.</summary>
    Deleted,

    /// <summary>Файл переименован; прежний путь — в <see cref="DiffFileEntry.OldPath"/>.</summary>
    Renamed,

    /// <summary>Новый файл, ещё не добавленный в git.</summary>
    Untracked,
}

/// <summary>
/// Почему файл свёрнут по умолчанию и не грузится, пока его не раскроют (модель GitHub, issue #5).
/// Бюджет автораскрытия сюда не входит: его считает страница по видимому после фильтра набору.
/// </summary>
public enum DiffCollapseReason
{
    /// <summary>Не свёрнут: раскрывается автоматически, пока хватает бюджета.</summary>
    None,

    /// <summary>Изменено больше порога строк.</summary>
    LargeDiff,

    /// <summary>Сгенерированный: <c>linguist-generated</c>/<c>-diff</c> или встроенный список.</summary>
    Generated,

    /// <summary>Бинарный: не раскрывается вовсе.</summary>
    Binary,
}

/// <summary>Строка оглавления diff.</summary>
/// <param name="Path">Путь относительно корня репозитория, через <c>/</c>.</param>
/// <param name="OldPath">Прежний путь у переименования, иначе <c>null</c>.</param>
/// <param name="Kind">Вид изменения.</param>
/// <param name="AddedLines">Добавлено строк; <c>null</c> — бинарный или неизвестно (новый файл сверх потолка подсчёта).</param>
/// <param name="DeletedLines">Удалено строк; <c>null</c> — бинарный или неизвестно.</param>
/// <param name="Collapse">Причина свёртки по умолчанию.</param>
public sealed record DiffFileEntry(
    string Path,
    string? OldPath,
    DiffChangeKind Kind,
    int? AddedLines,
    int? DeletedLines,
    DiffCollapseReason Collapse);

/// <summary>Что сравнивать.</summary>
/// <param name="Directory">Каталог внутри репозитория или worktree.</param>
/// <param name="BaseRef">
/// База сравнения; <c>null</c> — выбрать самому: <c>origin/HEAD</c>, затем <c>main</c>, затем <c>master</c>.
/// Сравнение идёт от <c>merge-base</c> базы и <c>HEAD</c> до рабочего дерева.
/// </param>
/// <param name="Files">
/// Файлы или каталоги, указанные агентом: сужают оглавление до них. Точно названные файлы
/// раскрываются всегда, вне бюджета; файлы под названным каталогом — в пределах бюджета.
/// Пусто — все изменённые.
/// </param>
/// <param name="IgnoreWhitespace">Сравнение без учёта пробелов (<c>-w</c>).</param>
public sealed record DiffRequest(string Directory, string? BaseRef, IReadOnlyList<string> Files, bool IgnoreWhitespace);

/// <summary>Оглавление diff: список изменённых файлов без содержимого.</summary>
/// <param name="RepositoryRoot">Корень рабочего дерева, в котором считался diff.</param>
/// <param name="BaseRef">База, против которой сравнивали (как её назвали или как выбрали).</param>
/// <param name="MergeBase">SHA общего предка — от него строится содержимое файлов.</param>
/// <param name="IgnoreWhitespace">С каким <c>-w</c> строилось; содержимое файлов берётся с тем же.</param>
/// <param name="Files">Изменённые файлы.</param>
public sealed record DiffIndex(
    string RepositoryRoot,
    string BaseRef,
    string MergeBase,
    bool IgnoreWhitespace,
    IReadOnlyList<DiffFileEntry> Files);

/// <summary>Сколько контекста показывать у файла.</summary>
public enum DiffContext
{
    /// <summary>Только изменённые фрагменты (<c>-U3</c>).</summary>
    Hunks,

    /// <summary>Весь файл: diff с полным контекстом.</summary>
    FullFile,
}

/// <summary>Содержимое diff одного файла.</summary>
/// <param name="Path">Путь файла, как в оглавлении.</param>
/// <param name="Context">С каким контекстом построен.</param>
/// <param name="Text">Unified diff одного файла. Уже обрезан по потолку, если <paramref name="Truncated"/>.</param>
/// <param name="Truncated">Вывод git превысил потолок и оборван; показывать «слишком большой».</param>
public sealed record FileDiff(string Path, DiffContext Context, string Text, bool Truncated);

/// <summary>Рабочее дерево репозитория из <c>git worktree list</c>.</summary>
/// <param name="Path">Каталог рабочего дерева.</param>
/// <param name="Branch">Ветка; <c>null</c> — detached HEAD.</param>
/// <param name="IsCurrent">Это дерево, в котором считался diff.</param>
public sealed record GitWorktree(string Path, string? Branch, bool IsCurrent);

/// <summary>Почему diff не построен.</summary>
public enum DiffFailure
{
    /// <summary><c>git</c> не найден.</summary>
    GitNotFound,

    /// <summary>Каталог не внутри репозитория.</summary>
    NotARepository,

    /// <summary>Базу не удалось определить или она не существует.</summary>
    NoBase,

    /// <summary>Git не уложился в отведённое время.</summary>
    Timeout,

    /// <summary>Git завершился с ошибкой.</summary>
    GitFailed,
}
