using System.Windows;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Запись в буфер обмена через <see cref="Clipboard"/> WPF. Занятый буфер не перехватывается:
/// <see cref="System.Runtime.InteropServices.ExternalException"/> уходит вызывающему, он и решает,
/// что сказать пользователю.
/// </summary>
public sealed class WpfClipboardWriter : IClipboardWriter
{
    /// <inheritdoc />
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Clipboard.SetText(text);
    }
}
