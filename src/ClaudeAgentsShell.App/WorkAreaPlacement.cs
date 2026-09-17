using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Вписывает окно в рабочую область монитора, на котором оно оказалось. Жёстко заданный
/// размер вылезает под панель задач на экранах, которые ниже этого размера, и последние
/// строки терминала — включая строку статуса Claude Code — уходят за край.
/// <para>
/// Рабочая область берётся у конкретного монитора, а не у основного: при нескольких
/// экранах с разным масштабом это разные величины. Пиксели устройства переводятся
/// в аппаратно-независимые единицы WPF по матрице того же окна, поэтому per-monitor DPI
/// учитывается автоматически.
/// </para>
/// </summary>
internal static class WorkAreaPlacement
{
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>
    /// Зажимает размер окна рабочей областью и ставит его по центру этой области.
    /// Вызывается после создания окна, когда уже есть дескриптор.
    /// </summary>
    internal static void FitIntoWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect workArea = ResolveWorkArea(window);
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        double width = Math.Min(window.Width, workArea.Width);
        double height = Math.Min(window.Height, workArea.Height);

        window.Width = width;
        window.Height = height;
        window.Left = workArea.Left + ((workArea.Width - width) / 2);
        window.Top = workArea.Top + ((workArea.Height - height) / 2);
    }

    private static Rect ResolveWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr monitor = handle == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(handle, MonitorDefaultToNearest);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            // Монитор определить не удалось — рабочая область основного экрана уже в единицах WPF.
            return SystemParameters.WorkArea;
        }

        Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        Point topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal int Flags;
    }
}
