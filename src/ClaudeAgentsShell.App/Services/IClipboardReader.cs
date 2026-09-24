namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Чтение системного буфера обмена для вставки не-текста. Отдельный порт, чтобы разбор
/// «что вставлять» тестировался с фейком, а мост не обращался к <c>Clipboard</c> напрямую.
/// </summary>
public interface IClipboardReader
{
    /// <summary>
    /// Снимок буфера на текущий момент. Может бросить <see cref="System.Runtime.InteropServices.ExternalException"/>,
    /// когда буфер занят другим процессом (<c>CLIPBRD_E_CANT_OPEN</c>): решение, что с этим делать, — за вызывающим.
    /// Вызывается только на STA-потоке окна.
    /// </summary>
    ClipboardSnapshot Read();
}

/// <summary>Что лежит в буфере обмена — только то, что нужно для вставки в терминал.</summary>
/// <param name="Files">Полные пути файлов, скопированных в проводнике (<c>CF_HDROP</c>); пусто, если их нет.</param>
/// <param name="HasImage">В буфере есть изображение.</param>
public sealed record ClipboardSnapshot(IReadOnlyList<string> Files, bool HasImage)
{
    /// <summary>Буфер пуст или в нём только то, что вставке не интересно.</summary>
    public static ClipboardSnapshot Empty { get; } = new([], false);
}
