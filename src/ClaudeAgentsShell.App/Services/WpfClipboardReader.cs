using System.Windows;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Буфер обмена через <see cref="Clipboard"/> WPF. Объект данных берётся один раз: проверки
/// «есть ли» и «дай» по отдельности открывали бы буфер дважды, и между ними его мог бы
/// перехватить другой процесс.
/// </summary>
public sealed class WpfClipboardReader : IClipboardReader
{
    /// <inheritdoc />
    public ClipboardSnapshot Read()
    {
        var data = Clipboard.GetDataObject();
        if (data is null)
        {
            return ClipboardSnapshot.Empty;
        }

        string[] files = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped
            ? dropped
            : [];

        bool hasImage = data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib);

        return new ClipboardSnapshot(files, hasImage);
    }
}
