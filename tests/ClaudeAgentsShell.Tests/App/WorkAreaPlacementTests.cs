using ClaudeAgentsShell.App;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Границы развёрнутого окна. Своё обрамление лишает окно системного поведения при
/// разворачивании, и без этого расчёта нижний край наслаивается на панель задач —
/// дефект, найденный на приёмке M1.
/// <para>
/// Проверяется чистая арифметика: сам обработчик <c>WM_GETMINMAXINFO</c> требует окна
/// и монитора, а GUI в сборке не запускается.
/// </para>
/// </summary>
public sealed class WorkAreaPlacementTests
{
    [Fact]
    public void Maximized_window_stops_at_the_taskbar_on_the_primary_monitor()
    {
        // Панель задач снизу: 40 физических пикселей.
        var monitor = new WorkAreaPlacement.NativeRect(0, 0, 1920, 1080);
        var work = new WorkAreaPlacement.NativeRect(0, 0, 1920, 1040);

        var bounds = WorkAreaPlacement.CalculateMaximizedBounds(monitor, work);

        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1040, bounds.Height);

        // Нижний край окна ровно на верхней границе панели задач, а не на границе экрана.
        Assert.Equal(work.Bottom, bounds.Top + bounds.Height);
    }

    [Fact]
    public void Maximized_window_uses_the_work_area_of_its_own_monitor()
    {
        // Второй монитор слева от основного: его координаты отрицательные. Положение
        // развёрнутого окна отсчитывается от угла монитора, а не от начала рабочего стола,
        // поэтому смещение монитора обязано вычитаться.
        var monitor = new WorkAreaPlacement.NativeRect(-1920, 0, 0, 1080);
        var work = new WorkAreaPlacement.NativeRect(-1920, 0, 0, 1040);

        var bounds = WorkAreaPlacement.CalculateMaximizedBounds(monitor, work);

        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1040, bounds.Height);
    }

    [Fact]
    public void A_taskbar_at_the_top_and_at_the_side_moves_the_maximized_corner()
    {
        // Панель задач слева (60 px) и сверху (48 px) на втором мониторе.
        var monitor = new WorkAreaPlacement.NativeRect(-1920, 0, 0, 1080);
        var work = new WorkAreaPlacement.NativeRect(-1860, 48, 0, 1080);

        var bounds = WorkAreaPlacement.CalculateMaximizedBounds(monitor, work);

        Assert.Equal(60, bounds.Left);
        Assert.Equal(48, bounds.Top);
        Assert.Equal(1860, bounds.Width);
        Assert.Equal(1032, bounds.Height);
    }

    [Fact]
    public void A_monitor_without_a_taskbar_is_filled_completely()
    {
        var monitor = new WorkAreaPlacement.NativeRect(1920, -200, 4480, 1240);
        var work = monitor;

        var bounds = WorkAreaPlacement.CalculateMaximizedBounds(monitor, work);

        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(2560, bounds.Width);
        Assert.Equal(1440, bounds.Height);
    }
}
