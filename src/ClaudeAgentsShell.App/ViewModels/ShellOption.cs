using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>Строка выпадающего списка «оболочка» в диалоге настроек проекта.</summary>
/// <param name="Kind">Значение, которое уедет в <c>projects.json</c>.</param>
/// <param name="Title">Что читает пользователь.</param>
/// <remarks>
/// Запись, а не класс, ради равенства по значению: <c>Selector.SelectedItem</c> ищет элемент
/// списка через <c>Equals</c>, и при ссылочном равенстве предвыбор оболочки молча отваливался бы —
/// дефект, который без запуска окна не увидеть.
/// </remarks>
public sealed record ShellOption(ShellKind Kind, string Title);

/// <summary>
/// Список оболочек для диалога. Строится по самому перечислению: новый элемент
/// <see cref="ShellKind"/> появляется в списке сам, без правки диалога.
/// </summary>
public static class ShellOptions
{
    private static readonly Dictionary<ShellKind, string> Titles = new()
    {
        [ShellKind.Pwsh] = "pwsh — PowerShell 7+",
        [ShellKind.WindowsPowerShell] = "powershell — Windows PowerShell 5.1",
        [ShellKind.Cmd] = "cmd — cmd.exe",
    };

    /// <summary>Все оболочки в порядке объявления перечисления.</summary>
    public static IReadOnlyList<ShellOption> All { get; } =
        [.. Enum.GetValues<ShellKind>().Select(static kind => new ShellOption(kind, TitleFor(kind)))];

    /// <summary>
    /// Строка списка для заданной оболочки. Значение вне перечисления (испорченный
    /// <c>projects.json</c>) не роняет диалог: для него собирается строка на лету.
    /// </summary>
    public static ShellOption For(ShellKind kind) =>
        All.FirstOrDefault(option => option.Kind == kind) ?? new ShellOption(kind, TitleFor(kind));

    private static string TitleFor(ShellKind kind) =>
        Titles.TryGetValue(kind, out var title) ? title : kind.ToString();
}
