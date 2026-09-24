using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Мигание кнопки главного окна через <c>FlashWindowEx</c>. Мигает системный таймер
/// (<c>FLASHW_TIMERNOFG</c>) — до тех пор, пока окно не выйдет на передний план; своего
/// таймера и опроса нет. Вызывать только из потока интерфейса.
/// </summary>
public sealed class TaskbarFlash : ITaskbarAttention
{
    private const uint FlashStop = 0;
    private const uint FlashTray = 0x2;
    private const uint FlashTimerNoForeground = 0xC;

    private readonly Func<Window?> _window;

    /// <param name="window">
    /// Главное окно, берётся лениво при каждом вызове. Делегат не должен создавать окно:
    /// пока его нет или у него ещё нет HWND, мигать нечем и вызовы ничего не делают.
    /// </param>
    public TaskbarFlash(Func<Window?> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <inheritdoc />
    /// <remarks>Повторный вызов перезапускает мигание с начала.</remarks>
    public void Request() => Flash(FlashTray | FlashTimerNoForeground);

    /// <inheritdoc />
    /// <remarks>
    /// Явный <c>FLASHW_STOP</c> обязателен: после окончания циклов Windows 11 оставляет
    /// кнопку подсвеченной, пока её не снимут.
    /// </remarks>
    public void Cancel() => Flash(FlashStop);

    private void Flash(uint flags)
    {
        if (_window() is not { } window)
        {
            return;
        }

        // Handle не создаёт HWND, а только читает его: у ещё не показанного окна он нулевой.
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = flags,
            Count = 0,
            Timeout = 0,
        };

        // Результат — прежнее состояние подсветки, а не успех вызова; проверять нечего.
        _ = FlashWindowEx(ref info);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Структура <c>FLASHWINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Flags;
        internal uint Count;
        internal uint Timeout;
    }
}
