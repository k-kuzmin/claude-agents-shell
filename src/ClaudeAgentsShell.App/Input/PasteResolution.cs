using System.Runtime.InteropServices;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Terminal.Protocol;

namespace ClaudeAgentsShell.App.Input;

/// <summary>
/// Что вставить в терминал, когда текста в буфере нет (<c>paste.request</c>) или на терминал
/// бросили файлы (<c>drop</c>). Чистые функции: буфер приходит портом, результат — в протокол.
/// </summary>
public static class PasteResolution
{
    /// <summary>
    /// Приоритет: файлы → изображение → ничего. PNG, скопированный в проводнике, — это файл,
    /// и вставляется его путь, а не картинка.
    /// </summary>
    /// <remarks>
    /// Занятый другим процессом буфер не отдаётся сразу: WPF-реализация
    /// (<c>Clipboard.GetDataObject</c>) сама повторяет открытие примерно 10 раз по 100 мс,
    /// и всё это время стоит UI-поток — пауза до ~1 с. Принято осознанно: это бывает только
    /// на <c>paste.request</c>, то есть по Ctrl+V без текста в буфере. Если буфер так и не
    /// открылся, ответ — «ничего».
    /// </remarks>
    public static PasteContent FromClipboard(IClipboardReader clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        ClipboardSnapshot snapshot;
        try
        {
            snapshot = clipboard.Read();
        }
        catch (ExternalException)
        {
            // COMException (CLIPBRD_E_CANT_OPEN и сбои отложенного рендеринга данных).
            return new PasteContent.None();
        }

        if (snapshot.Files.Count > 0)
        {
            return FromDrop(snapshot.Files);
        }

        return snapshot.HasImage ? new PasteContent.Image() : new PasteContent.None();
    }

    /// <summary>Пути брошенных или скопированных файлов; пустой список — «ничего».</summary>
    public static PasteContent FromDrop(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string text = FormatPaths(paths);
        return text.Length == 0 ? new PasteContent.None() : new PasteContent.Text(text);
    }

    /// <summary>
    /// Пути как в Windows Terminal: через один пробел, в двойных кавычках только те, где есть
    /// пробел, без завершающего пробела. Пустые элементы пропускаются.
    /// </summary>
    public static string FormatPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return string.Join(
            ' ',
            paths
                .Where(static path => !string.IsNullOrEmpty(path))
                .Select(static path => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path));
    }
}
