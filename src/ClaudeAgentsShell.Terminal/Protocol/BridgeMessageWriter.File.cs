using System.Globalization;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Сообщения режима «файл» панели вкладки (<c>show_file</c>, M8): <c>file.show</c> и
/// <c>file.content</c>. Не горячий путь; экранирование и нарезка — те же, что у <c>diff.*</c>.
/// Текст файла идёт строкой: это не вывод PTY, правило base64 его не касается.
/// </summary>
public sealed partial class BridgeMessageWriter
{
    /// <inheritdoc />
    public string FileShow(TerminalId terminalId, long sequence, FileViewSet files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = Head("file.show", terminalId, 128 + (files.Files.Count * 64));
        builder.Append(",\"seq\":").Append(sequence.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"root\":");
        AppendString(builder, files.Root);
        builder.Append(",\"note\":");
        AppendNullable(builder, files.Note);

        builder.Append(",\"files\":[");
        for (int i = 0; i < files.Files.Count; i++)
        {
            var file = files.Files[i];
            builder.Append(i > 0 ? ",{\"p\":" : "{\"p\":");
            AppendString(builder, file.Path);

            builder.Append(",\"focus\":");
            if (file.Focus is { } focus)
            {
                builder.Append("{\"from\":").Append(focus.From.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"to\":").Append(focus.To.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            else
            {
                builder.Append("null");
            }

            builder.Append(",\"problem\":");
            string? problem = ProblemCode(file);
            if (problem is null)
            {
                builder.Append("null}");
            }
            else
            {
                builder.Append('"').Append(problem).Append("\"}");
            }
        }

        return builder.Append("]}").ToString();
    }

    /// <inheritdoc />
    public IEnumerable<string> FileContent(TerminalId terminalId, long sequence, int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var head = Head("file.content", terminalId);
        head.Append(",\"seq\":").Append(sequence.ToString(CultureInfo.InvariantCulture));
        head.Append(",\"i\":").Append(index.ToString(CultureInfo.InvariantCulture));
        head.Append(",\"part\":");

        return TextParts(head.ToString(), string.Empty, text);
    }

    /// <summary>
    /// Код причины для страницы. Файл без причины, но и без текста (нарушение контракта
    /// <see cref="ViewedFile"/>) показывается как нечитаемый, а не «Загрузка…» навсегда.
    /// </summary>
    private static string? ProblemCode(ViewedFile file) => file.Problem switch
    {
        ViewedFileProblem.None when file.Text is not null => null,
        ViewedFileProblem.None => "unreadable",
        ViewedFileProblem.NotFound => "notFound",
        ViewedFileProblem.OutsideRoot => "outsideRoot",
        ViewedFileProblem.TooLarge => "tooLarge",
        ViewedFileProblem.Binary => "binary",
        _ => "unreadable",
    };

    /// <summary>
    /// Придёт ли для файла <c>file.content</c>: ровно тогда, когда в <c>file.show</c> у него
    /// <c>problem:null</c>. Мосту нужно то же правило, что и сообщению.
    /// </summary>
    public static bool HasContent(ViewedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ProblemCode(file) is null;
    }
}
