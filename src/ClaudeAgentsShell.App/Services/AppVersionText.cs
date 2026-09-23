namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Подписи версии для интерфейса: короткая в углу окна и полная во всплывающей подсказке.
/// </summary>
/// <param name="Label">Короткая подпись без суффикса сборки, например <c>v0.2.1</c>.</param>
/// <param name="ToolTip">Полная информационная версия, вместе с хэшем коммита.</param>
public sealed record AppVersionText(string Label, string ToolTip)
{
    private const string Unknown = "0.0.0";

    /// <summary>
    /// Собирает подписи из информационной версии: суффикс после <c>+</c> (метаданные сборки,
    /// у .NET 8 — хэш коммита) в короткой подписи отрезается, префикс <c>v</c> добавляется,
    /// если его ещё нет. Пустая версия показывается как <c>v0.0.0</c>.
    /// </summary>
    public static AppVersionText From(string? informationalVersion)
    {
        var full = informationalVersion?.Trim() ?? string.Empty;

        var plus = full.IndexOf('+', StringComparison.Ordinal);
        var core = (plus >= 0 ? full[..plus] : full).Trim();
        var metadata = plus >= 0 ? full[(plus + 1)..].Trim() : string.Empty;

        if (core.StartsWith('v') || core.StartsWith('V'))
        {
            core = core[1..];
        }

        if (core.Length == 0)
        {
            core = Unknown;
        }

        var label = "v" + core;
        var toolTip = metadata.Length == 0 ? "Версия " + core : $"Версия {core}+{metadata}";
        return new AppVersionText(label, toolTip);
    }
}
