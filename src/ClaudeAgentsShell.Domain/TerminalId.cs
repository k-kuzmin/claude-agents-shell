namespace ClaudeAgentsShell.Domain;

/// <summary>
/// Идентификатор терминала (вкладки) в протоколе моста C# ↔ страница.
/// Строка, потому что уходит в JSON как есть; сравнение — по значению.
/// </summary>
public readonly record struct TerminalId
{
    private readonly string? _value;

    /// <param name="value">Непустая строка без пробелов, например <c>t1</c>.</param>
    public TerminalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>Строковое представление, уходящее в сообщения моста.</summary>
    public string Value => _value ?? throw new InvalidOperationException("TerminalId не инициализирован.");

    /// <summary>Новый уникальный идентификатор вкладки.</summary>
    public static TerminalId New() => new("t" + Guid.NewGuid().ToString("N")[..12]);

    /// <inheritdoc />
    public override string ToString() => Value;
}
