namespace ClaudeAgentsShell.Terminal;

/// <summary>
/// Настройки горячего пути вывода. Значения по умолчанию — из разделов 3.3–3.5 ТЗ.
/// </summary>
public sealed record TerminalOptions
{
    /// <summary>Кадр склейки вывода: буфер сбрасывается не чаще одного раза за этот интервал.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>Порог размера, при котором буфер сбрасывается немедленно, не дожидаясь кадра.</summary>
    public int FlushThresholdBytes { get; init; } = 64 * 1024;

    /// <summary>Размер буфера одного чтения из PTY.</summary>
    public int ReadBufferBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// Сколько незавершённых <c>term.write</c> допускается на вкладку.
    /// При превышении чтение из PTY приостанавливается до подтверждения записи.
    /// </summary>
    public int MaxPendingWrites { get; init; } = 4;

    /// <summary>Строк истории на вкладку.</summary>
    public int Scrollback { get; init; } = 5000;

    /// <summary>Дебаунс ресайза, чтобы перетаскивание края окна не перерисовывало TUI на каждый пиксель.</summary>
    public TimeSpan ResizeDebounce { get; init; } = TimeSpan.FromMilliseconds(80);
}
