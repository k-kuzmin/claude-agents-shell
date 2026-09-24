namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Что вставить в терминал в ответ на <c>paste.request</c> или <c>drop</c>.
/// Форма в протоколе — <see cref="IBridgeMessageWriter.PasteResult"/>.
/// </summary>
public abstract record PasteContent
{
    private PasteContent()
    {
    }

    /// <summary>Готовый текст — например, пути файлов в кавычках через пробел.</summary>
    /// <param name="Value">Текст для <c>term.paste</c>.</param>
    public sealed record Text(string Value) : PasteContent;

    /// <summary>В буфере изображение: Claude Code прочитает его сам по Alt+V.</summary>
    public sealed record Image : PasteContent;

    /// <summary>Вставлять нечего.</summary>
    public sealed record None : PasteContent;
}
