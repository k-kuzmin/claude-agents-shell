namespace ClaudeAgentsShell.Domain;

/// <summary>Строки файла, к которым панель прокручивается и которые выделяет фоном.</summary>
/// <param name="From">Первая строка, с единицы.</param>
/// <param name="To">Последняя строка включительно; не меньше <paramref name="From"/>.</param>
public sealed record LineRange(int From, int To);

/// <summary>Почему файл не показан содержимым.</summary>
public enum ViewedFileProblem
{
    /// <summary>Файл прочитан, текст в <see cref="ViewedFile.Text"/>.</summary>
    None,

    /// <summary>Файла нет или это каталог.</summary>
    NotFound,

    /// <summary>Путь ведёт за пределы корня (в том числе через ссылку или junction).</summary>
    OutsideRoot,

    /// <summary>Файл больше предела показа (4 МБ, как у diff).</summary>
    TooLarge,

    /// <summary>Двоичный файл.</summary>
    Binary,

    /// <summary>Файл не удалось прочитать: занят, нет прав.</summary>
    Unreadable,
}

/// <summary>Один файл для просмотра без diff: только текст и подсветка синтаксиса.</summary>
/// <param name="Path">Путь относительно <see cref="FileViewSet.Root"/>, через <c>/</c>.</param>
/// <param name="Text">Содержимое; <c>null</c>, если <paramref name="Problem"/> не <see cref="ViewedFileProblem.None"/>.</param>
/// <param name="Focus">Строки, к которым прокрутить и которые выделить; <c>null</c> — с начала файла.</param>
/// <param name="Problem">Почему нет текста.</param>
public sealed record ViewedFile(string Path, string? Text, LineRange? Focus, ViewedFileProblem Problem);

/// <summary>Набор файлов, который агент попросил показать вызовом <c>show_file</c>.</summary>
/// <param name="Root">Корень, от которого считаются пути: верх репозитория или каталог сессии.</param>
/// <param name="Note">Пояснение агента над списком; <c>null</c> — нет.</param>
/// <param name="Files">Файлы в порядке, в котором их назвал агент.</param>
public sealed record FileViewSet(string Root, string? Note, IReadOnlyList<ViewedFile> Files);
