namespace ClaudeAgentsShell.Domain;

/// <summary>Размер терминала в знакоместах. Нулевые размеры недопустимы — ConPTY на них ломается.</summary>
/// <param name="Cols">Столбцов, не меньше 1.</param>
/// <param name="Rows">Строк, не меньше 1.</param>
public readonly record struct TerminalSize(int Cols, int Rows)
{
    /// <summary>Размер, с которого стартует PTY, если страница ещё не прислала реальный.</summary>
    public static TerminalSize Default { get; } = new(120, 34);

    /// <summary>Размер осмыслен: обе величины положительные и не запредельные.</summary>
    public bool IsValid => Cols is > 0 and <= 1000 && Rows is > 0 and <= 1000;
}
