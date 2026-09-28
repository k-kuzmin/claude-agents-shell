namespace ClaudeAgentsShell.App.Services;

/// <summary>Запись текста в системный буфер обмена. Порт, чтобы ViewModel не трогала <c>Clipboard</c>.</summary>
public interface IClipboardWriter
{
    /// <summary>
    /// Кладёт текст в буфер. Может бросить <see cref="System.Runtime.InteropServices.ExternalException"/>,
    /// когда буфер занят другим процессом. Вызывается только на STA-потоке окна.
    /// </summary>
    void SetText(string text);
}
