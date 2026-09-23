using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Решает, свёрнут ли файл по умолчанию, — модель GitHub без ручной настройки (issue #5).
/// Порядок: названный агентом → бинарный → сгенерированный → большой. Бюджет автораскрытия сюда не входит —
/// его считает страница по видимому после фильтра набору.
/// </summary>
public sealed class DiffCollapsePolicy
{
    private static readonly string[] GeneratedFileNames = ["package-lock.json", "yarn.lock", "pnpm-lock.yaml"];
    private static readonly string[] GeneratedExtensions = [".lock", ".map"];

    private readonly GitDiffOptions _options;

    /// <inheritdoc cref="DiffCollapsePolicy" />
    public DiffCollapsePolicy(GitDiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Причина свёртки файла.</summary>
    /// <param name="entry">Строка оглавления; её собственная <see cref="DiffFileEntry.Collapse"/> не учитывается.</param>
    /// <param name="attributes">Атрибуты пути из <c>.gitattributes</c>.</param>
    /// <param name="requested">
    /// Файл назван агентом в <c>files</c>: не сворачивается никогда, даже бинарный —
    /// его страница узнаёт по счётчикам <c>null</c>/<c>null</c>.
    /// </param>
    public DiffCollapseReason Classify(DiffFileEntry entry, GitPathAttributes attributes, bool requested)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (requested)
        {
            return DiffCollapseReason.None;
        }

        if (IsBinary(entry))
        {
            return DiffCollapseReason.Binary;
        }

        if (IsGenerated(entry.Path, attributes))
        {
            return DiffCollapseReason.Generated;
        }

        if (entry.AddedLines is null)
        {
            // Новый файл больше потолка подсчёта — заведомо большой.
            return DiffCollapseReason.LargeDiff;
        }

        var changed = (long)(entry.AddedLines ?? 0) + (entry.DeletedLines ?? 0);
        return changed > _options.LargeDiffLineThreshold ? DiffCollapseReason.LargeDiff : DiffCollapseReason.None;
    }

    /// <summary>
    /// Сгенерированный: <c>linguist-generated</c> задан (<c>set</c>/<c>true</c>), у пути <c>-diff</c>,
    /// или имя из встроенного списка. Явный <c>linguist-generated=false</c> снимает встроенный список.
    /// </summary>
    public static bool IsGenerated(string path, GitPathAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(path);

        switch (attributes.LinguistGenerated)
        {
            case "set" or "true":
                return true;
            case "unset" or "false":
                return false;
        }

        return attributes.Diff == "unset" || IsBuiltInGenerated(path);
    }

    /// <summary>
    /// Встроенный список сгенерированных: <c>package-lock.json</c>, <c>yarn.lock</c>,
    /// <c>pnpm-lock.yaml</c>, <c>*.lock</c>, <c>*.min.*</c>, <c>*.map</c>. Без UI и настройки.
    /// </summary>
    public static bool IsBuiltInGenerated(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path[(path.LastIndexOf('/') + 1)..];

        foreach (var generated in GeneratedFileNames)
        {
            if (name.Equals(generated, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var extension in GeneratedExtensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // *.min.* — «.min.» где угодно после первого символа имени: app.min.js, style.min.css.
        return name.Length > 1 && name.IndexOf(".min.", 1, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Оба счётчика <c>null</c> — бинарный. У нового файла сверх потолка подсчёта неизвестно
    /// только добавленное (<c>AddedLines = null</c>, <c>DeletedLines = 0</c>), он не бинарный.
    /// </summary>
    private static bool IsBinary(DiffFileEntry entry) => entry.AddedLines is null && entry.DeletedLines is null;
}
