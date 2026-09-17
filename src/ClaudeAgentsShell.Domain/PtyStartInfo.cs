namespace ClaudeAgentsShell.Domain;

/// <summary>Всё, что нужно для запуска процесса в псевдоконсоли.</summary>
/// <param name="Shell">Разрешённая оболочка.</param>
/// <param name="WorkingDirectory">Рабочий каталог процесса.</param>
/// <param name="InitialSize">Стартовый размер псевдоконсоли.</param>
/// <param name="Environment">
/// Переменные, добавляемые к окружению процесса. Обязательно содержит <c>TERM=xterm-256color</c>.
/// </param>
public sealed record PtyStartInfo(
    ShellStartCommand Shell,
    string WorkingDirectory,
    TerminalSize InitialSize,
    IReadOnlyDictionary<string, string> Environment);
