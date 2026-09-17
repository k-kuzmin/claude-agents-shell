namespace ClaudeAgentsShell.Domain;

/// <summary>Разрешённая оболочка: чем именно запускать процесс внутри ConPTY.</summary>
/// <param name="Kind">Какая оболочка в итоге найдена — может отличаться от запрошенной из-за отката.</param>
/// <param name="FileName">Полный путь к исполняемому файлу.</param>
/// <param name="Arguments">Аргументы командной строки оболочки.</param>
public sealed record ShellStartCommand(ShellKind Kind, string FileName, IReadOnlyList<string> Arguments);
