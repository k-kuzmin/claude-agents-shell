using System.Diagnostics;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Снятие дерева процессов git, упорядоченное с освобождением процесса.
/// <list type="bullet">
/// <item><see cref="Request"/> вызывается из колбэка отмены (часто из потока интерфейса) и сразу
/// возвращается: само снятие (~13 мс) идёт в пуле.</item>
/// <item><see cref="Close"/> вызывается перед <c>Process.Dispose</c>: после него снятие не
/// начинается, а уже идущее он дожидается. Хендл не закрывается посреди <c>TerminateProcess</c>.</item>
/// <item>Любое исключение снятия глотается: необработанное исключение в пуле уронило бы приложение.</item>
/// </list>
/// </summary>
public sealed class GitProcessTermination
{
    private readonly object _lock = new();
    private readonly Action _kill;
    private bool _closed;

    /// <inheritdoc cref="GitProcessTermination" />
    /// <param name="kill">Само снятие; может бросать что угодно.</param>
    public GitProcessTermination(Action kill)
    {
        ArgumentNullException.ThrowIfNull(kill);
        _kill = kill;
    }

    /// <summary>Снятие процесса вместе с потомками.</summary>
    public static GitProcessTermination For(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new GitProcessTermination(() => process.Kill(entireProcessTree: true));
    }

    /// <summary>Ставит снятие в пул и сразу возвращается.</summary>
    public void Request() =>
        ThreadPool.UnsafeQueueUserWorkItem(static self => self.KillNow(), this, preferLocal: false);

    /// <summary>
    /// Снимает процесс в текущем потоке, если <see cref="Close"/> ещё не вызван.
    /// Исключения не выпускает.
    /// </summary>
    public void KillNow()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _kill();
            }
            catch (Exception)
            {
                // Процесс уже завершился, доступ закрыт, AggregateException от дерева процессов —
                // снимать больше нечего, а исключение в пуле уронило бы приложение.
            }
        }
    }

    /// <summary>Запрещает дальнейшие снятия и дожидается идущего.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }
}
