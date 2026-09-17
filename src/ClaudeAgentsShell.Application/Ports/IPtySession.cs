using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Аргументы завершения процесса в псевдоконсоли.</summary>
/// <param name="exitCode">Код выхода оболочки.</param>
public sealed class PtyExitedEventArgs(int exitCode) : EventArgs
{
    /// <summary>Код выхода оболочки.</summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Одна псевдоконсоль: оболочка, поднятая через ConPTY, её stdin/stdout и размер.
/// О вкладках, проектах и Claude Code не знает ничего.
/// </summary>
public interface IPtySession : IAsyncDisposable
{
    /// <summary>Процесс оболочки ещё жив.</summary>
    bool IsRunning { get; }

    /// <summary>Код выхода; <c>null</c>, пока процесс жив.</summary>
    int? ExitCode { get; }

    /// <summary>Процесс оболочки завершился. Вкладка закрывается только по этому событию.</summary>
    event EventHandler<PtyExitedEventArgs>? Exited;

    /// <summary>
    /// Читает очередную порцию сырых байтов вывода в переданный буфер.
    /// Возвращает 0, когда поток закрыт. Байты не декодируются в строку: чанк может
    /// оборваться посреди UTF-8 последовательности.
    /// Вызывающий владеет буфером и темпом чтения — так реализуется backpressure.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Пишет сырые байты в stdin псевдоконсоли.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Меняет размер псевдоконсоли. Нулевые размеры отбрасываются вызывающим.</summary>
    void Resize(TerminalSize size);
}
