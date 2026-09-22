namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Пороги чтения diff (issue #5). Значения по умолчанию — стартовые, уточняются на приёмке.
/// </summary>
public sealed record GitDiffOptions
{
    /// <summary>Исполняемый файл git; ищется по <c>PATH</c>.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>Файл, у которого добавлено и удалено вместе больше стольких строк, свёрнут как «Большой diff».</summary>
    public int LargeDiffLineThreshold { get; init; } = 400;

    /// <summary>
    /// Потолок на diff одного файла и на чтение неотслеживаемого файла, в байтах.
    /// Сверх него процесс снимается, а результат помечается «слишком большой».
    /// </summary>
    public int FileOutputCeilingBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Сколько байт начала неотслеживаемого файла проверяется на NUL, чтобы признать его бинарным.</summary>
    public int BinarySniffBytes { get; init; } = 8 * 1024;

    /// <summary>Сколько даётся одной операции целиком — оглавлению, файлу или списку worktree.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
