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

    /// <summary>
    /// Строк истории, остающихся у вкладки после выхода процесса.
    /// <para>
    /// Мёртвая вкладка не закрывается сама (раздел 8 ТЗ) и переживает перезапуск сессии:
    /// «перезапустить» открывает новую вкладку, а прежняя остаётся на экране ради кода
    /// выхода и последних строк вывода. Полный <see cref="Scrollback"/> ей при этом не
    /// нужен — писать в неё больше некому, а буфер xterm.js при 5000 строк и двух сотнях
    /// колонок стоит порядка десятка мегабайт и не освобождается до закрытия вкладки.
    /// Значение больше <see cref="Scrollback"/> смысла не имеет: страница берёт меньшее
    /// из двух.
    /// </para>
    /// </summary>
    public int ExitedScrollback { get; init; } = 500;

    /// <summary>Дебаунс ресайза, чтобы перетаскивание края окна не перерисовывало TUI на каждый пиксель.</summary>
    public TimeSpan ResizeDebounce { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Сколько ждать первого байта вывода оболочки перед записью команды запуска в stdin.
    /// По истечении срока команда пишется всё равно: молчащая оболочка — это ещё не причина
    /// терять запуск сессии.
    /// </summary>
    public TimeSpan StartupOutputTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Пауза между первым байтом вывода оболочки и записью команды запуска.
    /// <para>
    /// Надёжного признака готовности readline у нас нет, а разбирать вывод в поисках
    /// приглашения запрещено (раздел 7 CLAUDE.md), поэтому это именно запас по времени.
    /// Значение подобрано на глаз и проверяется только приёмкой «открыть пять сессий подряд
    /// и убедиться, что команда доехала целиком»; ноль отключает паузу.
    /// </para>
    /// </summary>
    public TimeSpan StartupInputDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}
