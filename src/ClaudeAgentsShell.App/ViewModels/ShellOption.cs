using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>Строка выпадающего списка «оболочка» в диалоге настроек проекта.</summary>
/// <param name="Kind">Значение, которое уедет в <c>projects.json</c>.</param>
/// <param name="Title">Что читает пользователь: название плюс пометка о недоступности.</param>
/// <remarks>
/// Запись, а не класс, ради равенства по значению: <c>Selector.SelectedItem</c> ищет элемент
/// списка через <c>Equals</c>, и при ссылочном равенстве предвыбор оболочки молча отваливался бы —
/// дефект, который без запуска окна не увидеть. По той же причине пометка о недоступности
/// вшита в <see cref="Title"/>: список пересобирается целиком, когда проверка отвечает,
/// и новый элемент обязан совпасть с выбранным по значению.
/// </remarks>
public sealed record ShellOption(ShellKind Kind, string Title);

/// <summary>
/// Список оболочек для диалога. Строится по самому перечислению: новый элемент
/// <see cref="ShellKind"/> появляется в списке сам, без правки диалога. Оболочки, которых
/// нет в системе, из списка не убираются, но помечены: раздел 8 ТЗ требует отражать откат
/// в настройках проекта, а не прятать выбор.
/// </summary>
public static class ShellOptions
{
    /// <summary>Пометка у оболочки, которой нет в системе.</summary>
    public const string MissingMark = " · не установлена";

    private static readonly Dictionary<ShellKind, string> Titles = new()
    {
        [ShellKind.Pwsh] = "pwsh — PowerShell 7+",
        [ShellKind.WindowsPowerShell] = "powershell — Windows PowerShell 5.1",
        [ShellKind.Cmd] = "cmd — cmd.exe",
    };

    /// <summary>Собирает список для диалога.</summary>
    /// <param name="selected">
    /// Выбранная оболочка. Значение вне перечисления (испорченный <c>projects.json</c>)
    /// не теряется и не роняет диалог: для него добавляется отдельная строка.
    /// </param>
    /// <param name="installed">
    /// Оболочки, найденные в системе, либо <c>null</c>, если проверка ещё не отвечала
    /// или не удалась. <c>null</c> — значит не помечать ничего: выдумать недоступность хуже,
    /// чем промолчать.
    /// </param>
    public static IReadOnlyList<ShellOption> Build(ShellKind selected, IReadOnlyList<ShellKind>? installed)
    {
        var kinds = Enum.GetValues<ShellKind>().ToList();
        if (!kinds.Contains(selected))
        {
            kinds.Add(selected);
        }

        return [.. kinds.Select(kind => new ShellOption(kind, TitleWithMark(kind, installed)))];
    }

    /// <summary>Название оболочки без пометок. Нужно сообщению об откате.</summary>
    public static string TitleFor(ShellKind kind) =>
        Titles.TryGetValue(kind, out var title) ? title : kind.ToString();

    private static string TitleWithMark(ShellKind kind, IReadOnlyList<ShellKind>? installed) =>
        installed is not null && !installed.Contains(kind)
            ? TitleFor(kind) + MissingMark
            : TitleFor(kind);
}
