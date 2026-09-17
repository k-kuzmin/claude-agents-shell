namespace ClaudeAgentsShell.Sessions;

/// <summary>
/// Настройки слоя сессий. Значения по умолчанию соответствуют разделам 4.1 и 6.2 ТЗ.
/// </summary>
public sealed record SessionsOptions
{
    /// <summary>
    /// Дебаунс наблюдателя за веткой. Одна операция git трогает <c>.git</c> многократно
    /// (например, <c>HEAD.lock</c> и переименование поверх <c>HEAD</c>), поэтому события
    /// склеиваются в одно чтение.
    /// </summary>
    public TimeSpan GitBranchDebounce { get; init; } = TimeSpan.FromMilliseconds(250);
}
