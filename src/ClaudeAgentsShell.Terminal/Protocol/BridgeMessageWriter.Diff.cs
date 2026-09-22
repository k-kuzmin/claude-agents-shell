using System.Globalization;
using System.Text;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Сообщения панели diff (issue #5, форма — раздел M7 <c>docs/PROGRESS.md</c>). Не горячий путь:
/// собираются по запросу человека или агента, поэтому пишутся простым <see cref="StringBuilder"/>.
/// Экранирование своё, а не <c>JsonEncodedText</c>: кириллица остаётся как есть (сообщение
/// короче вдвое-вшестеро), длина экранированного символа известна заранее — на ней построена
/// нарезка <c>diff.file</c>, — а одиночный суррогат заменяется на U+FFFD вместо исключения.
/// </summary>
public sealed partial class BridgeMessageWriter
{
    /// <summary>
    /// Потолок длины одного сообщения <c>diff.file</c> в символах UTF-16 — именно столько
    /// строка весит в <c>PostWebMessageAsString</c>. Стартовое значение issue #5 («≤ 1 МБ»).
    /// </summary>
    public const int MaxDiffMessageLength = 1024 * 1024;

    private const string LastField = ",\"last\":";
    private const string TruncatedField = ",\"truncated\":";
    private const string TextField = ",\"text\":\"";

    /// <inheritdoc />
    public string DiffPending(TerminalId terminalId) => Simple("diff.pending", terminalId);

    /// <inheritdoc />
    public string DiffStale(TerminalId terminalId) => Simple("diff.stale", terminalId);

    /// <inheritdoc />
    public string DiffError(TerminalId terminalId, string? path, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = Head("diff.error", terminalId);
        builder.Append(",\"path\":");
        AppendNullable(builder, path);
        builder.Append(",\"message\":");
        AppendString(builder, message);
        return builder.Append('}').ToString();
    }

    /// <inheritdoc />
    public string DiffIndex(
        TerminalId terminalId,
        DiffIndex index,
        IReadOnlyList<GitWorktree> worktrees,
        string? note,
        IReadOnlyList<string> expandFiles)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(expandFiles);

        // Примерно 64 символа на файл — чтобы тысячи .meta не перевыделяли буфер десятки раз.
        var builder = Head("diff.index", terminalId, 256 + (index.Files.Count * 64));

        builder.Append(",\"root\":");
        AppendString(builder, index.RepositoryRoot);
        builder.Append(",\"base\":");
        AppendString(builder, index.BaseRef);
        builder.Append(",\"mergeBase\":");
        AppendString(builder, index.MergeBase);
        builder.Append(",\"ws\":").Append(index.IgnoreWhitespace ? "true" : "false");
        builder.Append(",\"note\":");
        AppendNullable(builder, note);

        builder.Append(",\"expand\":[");
        for (int i = 0; i < expandFiles.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendString(builder, expandFiles[i]);
        }

        builder.Append("],\"worktrees\":[");
        for (int i = 0; i < worktrees.Count; i++)
        {
            var worktree = worktrees[i];
            builder.Append(i > 0 ? ",{\"path\":" : "{\"path\":");
            AppendString(builder, worktree.Path);
            builder.Append(",\"branch\":");
            AppendNullable(builder, worktree.Branch);
            builder.Append(",\"current\":").Append(worktree.IsCurrent ? "true}" : "false}");
        }

        builder.Append("],\"files\":[");
        for (int i = 0; i < index.Files.Count; i++)
        {
            var file = index.Files[i];
            builder.Append(i > 0 ? ",{\"p\":" : "{\"p\":");
            AppendString(builder, file.Path);
            builder.Append(",\"o\":");
            AppendNullable(builder, file.OldPath);
            builder.Append(",\"k\":\"").Append(KindCode(file.Kind));
            builder.Append("\",\"a\":");
            AppendCount(builder, file.AddedLines);
            builder.Append(",\"d\":");
            AppendCount(builder, file.DeletedLines);
            builder.Append(",\"c\":\"").Append(CollapseCode(file.Collapse)).Append("\"}");
        }

        return builder.Append("]}").ToString();
    }

    /// <inheritdoc />
    public IEnumerable<string> DiffFile(TerminalId terminalId, FileDiff file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return DiffFileParts(terminalId, file);
    }

    private static IEnumerable<string> DiffFileParts(TerminalId terminalId, FileDiff file)
    {
        var head = Head("diff.file", terminalId);
        head.Append(",\"path\":");
        AppendString(head, file.Path);
        head.Append(",\"ctx\":\"").Append(file.Context == DiffContext.FullFile ? "full" : "hunks");
        head.Append("\",\"part\":");
        string prefix = head.ToString();

        string truncated = file.Truncated ? "true" : "false";
        string text = file.Text;
        int remaining = EscapedLength(text, 0, text.Length);
        int position = 0;

        for (int part = 0; ; part++)
        {
            string number = part.ToString(CultureInfo.InvariantCulture);
            int overhead = prefix.Length + number.Length + LastField.Length + TruncatedField.Length
                + truncated.Length + TextField.Length + 2; // "}

            bool last = overhead + 4 + remaining <= MaxDiffMessageLength; // true
            int end = last ? text.Length : SliceEnd(text, position, MaxDiffMessageLength - overhead - 5); // false
            int escaped = EscapedLength(text, position, end);

            var builder = new StringBuilder(overhead + 5 + escaped);
            builder.Append(prefix).Append(number)
                .Append(LastField).Append(last ? "true" : "false")
                .Append(TruncatedField).Append(truncated)
                .Append(TextField);
            AppendEscaped(builder, text, position, end);
            builder.Append("\"}");

            yield return builder.ToString();

            if (last)
            {
                yield break;
            }

            remaining -= escaped;
            position = end;
        }
    }

    /// <summary>
    /// Конец части, начатой в <paramref name="start"/>: столько символов, сколько помещается
    /// в <paramref name="budget"/> экранированных. Суррогатная пара — неделимая единица.
    /// Хотя бы одна единица берётся всегда, иначе нарезка не продвинулась бы.
    /// </summary>
    private static int SliceEnd(string text, int start, int budget)
    {
        int used = 0;
        int i = start;

        while (i < text.Length)
        {
            int width = UnitWidth(text, i, out int step);
            if (used + width > budget && i > start)
            {
                break;
            }

            used += width;
            i += step;
        }

        return i;
    }

    private static int EscapedLength(string text, int start, int end)
    {
        int length = 0;
        for (int i = start; i < end;)
        {
            length += UnitWidth(text, i, out int step);
            i += step;
        }

        return length;
    }

    /// <summary>
    /// Длина экранированной единицы, начинающейся в <paramref name="index"/>, и сколько символов
    /// исходника она занимает. Правила ровно те же, что у <see cref="AppendEscaped"/>.
    /// </summary>
    private static int UnitWidth(string text, int index, out int step)
    {
        char c = text[index];
        step = 1;

        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            step = 2;
            return 2;
        }

        if (char.IsSurrogate(c))
        {
            return 6; // �
        }

        return c switch
        {
            '"' or '\\' or '\n' or '\r' or '\t' or '\b' or '\f' => 2,
            < ' ' => 6,
            _ => 1,
        };
    }

    private static void AppendEscaped(StringBuilder builder, string text, int start, int end)
    {
        int runStart = start;

        for (int i = start; i < end; i++)
        {
            char c = text[i];

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            string? escape = c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                _ when char.IsSurrogate(c) => "\\ufffd",
                < ' ' => null,
                _ => string.Empty,
            };

            if (escape is { Length: 0 })
            {
                continue;
            }

            builder.Append(text, runStart, i - runStart);
            if (escape is null)
            {
                builder.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(escape);
            }

            runStart = i + 1;
        }

        builder.Append(text, runStart, end - runStart);
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        AppendEscaped(builder, value, 0, value.Length);
        builder.Append('"');
    }

    private static void AppendNullable(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("null");
            return;
        }

        AppendString(builder, value);
    }

    private static void AppendCount(StringBuilder builder, int? value)
    {
        if (value is { } count)
        {
            builder.Append(count.ToString(CultureInfo.InvariantCulture));
            return;
        }

        builder.Append("null");
    }

    private static StringBuilder Head(string type, TerminalId terminalId, int capacity = 64) =>
        new StringBuilder(capacity)
            .Append("{\"type\":\"").Append(type)
            .Append("\",\"id\":\"").Append(JsonStringEscape.Escape(terminalId.Value)).Append('"');

    private static string Simple(string type, TerminalId terminalId) =>
        Head(type, terminalId).Append('}').ToString();

    private static string KindCode(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Added => "A",
        DiffChangeKind.Deleted => "D",
        DiffChangeKind.Renamed => "R",
        DiffChangeKind.Untracked => "U",
        _ => "M",
    };

    private static string CollapseCode(DiffCollapseReason reason) => reason switch
    {
        DiffCollapseReason.LargeDiff => "large",
        DiffCollapseReason.Generated => "generated",
        DiffCollapseReason.Binary => "binary",
        _ => "none",
    };
}
