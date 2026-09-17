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
    private const int WmGetMinMaxInfo = 0x0024;

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

    /// <summary>
    /// Учит окно разворачиваться в рабочую область своего монитора, а не на весь экран.
    /// Нужно из-за собственного обрамления: окно без системной рамки система разворачивает
    /// по границам монитора, и нижний край наслаивается на панель задач.
    /// Вызывается один раз после создания окна, когда уже есть дескриптор.
    /// </summary>
    internal static void KeepMaximizedWithinWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Хук снимать не нужно: он статический, ничего не захватывает и живёт ровно
        // столько же, сколько источник окна.
        (PresentationSource.FromVisual(window) as HwndSource)?.AddHook(OnWindowMessage);
    }

    /// <summary>
    /// Считает положение и размер развёрнутого окна. Обе величины — в физических пикселях
    /// и относительно монитора: <c>WM_GETMINMAXINFO</c> ждёт именно их, поэтому здесь,
    /// в отличие от <see cref="FitIntoWorkArea"/>, нет перевода в единицы WPF. Перевод
    /// сломал бы разворачивание на мониторе с масштабом, отличным от 100 %.
    /// </summary>
    /// <param name="monitor">Границы монитора.</param>
    /// <param name="work">Рабочая область того же монитора — экран за вычетом панели задач.</param>
    internal static MaximizedBounds CalculateMaximizedBounds(in NativeRect monitor, in NativeRect work) =>
        new(
            work.Left - monitor.Left,
            work.Top - monitor.Top,
            work.Right - work.Left,
            work.Bottom - work.Top);

    private static IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            // Монитор не отвечает — пусть окно развернётся как умеет: это хуже панели задач,
            // но лучше окна, которое не разворачивается вовсе.
            return IntPtr.Zero;
        }

        var bounds = CalculateMaximizedBounds(info.Monitor, info.Work);

        // Структура приходит уже заполненной: WPF успел положить туда MinTrackSize
        // из MinWidth и MinHeight окна. Трогаем только то, что относится к развёрнутому
        // состоянию, — иначе пропадёт минимальный размер окна.
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition = new NativePoint(bounds.Left, bounds.Top);
        minMax.MaxSize = new NativePoint(bounds.Width, bounds.Height);
        minMax.MaxTrackSize = minMax.MaxSize;
        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);

        handled = true;
        return IntPtr.Zero;
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

    /// <summary>Прямоугольник Windows в физических пикселях.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect(int left, int top, int right, int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    /// <summary>Точка Windows в физических пикселях.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    /// <summary>
    /// Где и какого размера окажется развёрнутое окно. Положение — относительно левого
    /// верхнего угла монитора, как того требует <c>WM_GETMINMAXINFO</c>.
    /// </summary>
    internal readonly record struct MaximizedBounds(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaxSize;
        internal NativePoint MaxPosition;
        internal NativePoint MinTrackSize;
        internal NativePoint MaxTrackSize;
    }
}
