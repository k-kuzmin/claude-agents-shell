using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>Итог одного запуска git.</summary>
/// <param name="ExitCode">Код выхода; <c>null</c>, если процесс снят по потолку вывода.</param>
/// <param name="Output">Stdout как есть, в байтах. При <paramref name="Truncated"/> — первые байты до потолка.</param>
/// <param name="Truncated">Вывод превысил потолок, процесс снят.</param>
/// <param name="Error">Stderr, обрезанный до нескольких килобайт, — для сообщения о сбое.</param>
public sealed record GitProcessResult(int? ExitCode, byte[] Output, bool Truncated, string Error)
{
    /// <summary>Процесс завершился сам и с нулевым кодом.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Запускает git только для чтения: без окна, с <c>GIT_OPTIONAL_LOCKS=0</c> (git не обновляет
/// индекс попутно) и <c>core.quotepath=false</c> (кириллица в путях без экранирования).
/// Отмена снимает дерево процессов; живых git после возврата не остаётся.
/// <para>
/// Пайпы процесса синхронные: асинхронное чтение из них держало бы поток пула всё время работы
/// git. Поэтому stdout, stderr и stdin обслуживают выделенные потоки, а одновременно работающих
/// git не больше <see cref="GitDiffOptions.MaxConcurrentProcesses"/> на всё приложение
/// (<see cref="GitProcessGate"/>): медленный git не отнимает пул у склейки вывода терминалов.
/// </para>
/// </summary>
public sealed class GitProcessRunner
{
    private const int ChunkSize = 64 * 1024;
    private const int ErrorLimitChars = 4 * 1024;

    // Потокам пайпов хватает малого стека: они только копируют буферы.
    private const int PipeThreadStackBytes = 256 * 1024;

    private static readonly string[] CommonArguments =
    [
        "--no-pager",
        "--literal-pathspecs",
        "-c", "core.quotepath=false",

        // Встроенный fsmonitor запускает демона, который переживает команду.
        "-c", "core.fsmonitor=false",
    ];

    private readonly GitDiffOptions _options;
    private readonly GitProcessGate _gate;

    /// <inheritdoc cref="GitProcessRunner" />
    /// <param name="options">Исполняемый файл git.</param>
    /// <param name="gate">Общий на приложение предел одновременных git.</param>
    public GitProcessRunner(GitDiffOptions options, GitProcessGate gate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        _options = options;
        _gate = gate;
    }

    /// <summary>Запускает git и собирает вывод целиком или до потолка.</summary>
    /// <param name="workingDirectory">Каталог запуска; пути в аргументах считаются от него.</param>
    /// <param name="arguments">Аргументы после общих (<c>-c core.quotepath=false</c> и пр.).</param>
    /// <param name="standardInput">Что подать на stdin; stdin закрывается после записи в любом случае.</param>
    /// <param name="outputCeilingBytes">Потолок stdout; <c>null</c> — без потолка.</param>
    /// <param name="cancellationToken">Отмена снимает процесс вместе с потомками или убирает запрос из очереди.</param>
    /// <exception cref="DiffUnavailableException">git не найден (<see cref="DiffFailure.GitNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException">Запрос отменён.</exception>
    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        int? outputCeilingBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        using var lease = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments) };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new DiffUnavailableException(DiffFailure.GitNotFound, "git не найден. Установите Git и добавьте его в PATH.", exception);
        }

        var termination = GitProcessTermination.For(process);
        try
        {
            // Отменяют и из потока интерфейса, а снятие дерева занимает ~13 мс — уводим его в пул.
            using var registration = cancellationToken.Register(
                static state => ((GitProcessTermination)state!).Request(),
                termination);

            var stdinTask = OnDedicatedThread(() => WriteInput(process, standardInput), "git stdin");
            var stderrTask = OnDedicatedThread(() => ReadError(process), "git stderr");
            var (output, overflow) = await OnDedicatedThread(
                () => ReadOutput(process.StandardOutput.BaseStream, outputCeilingBytes), "git stdout").ConfigureAwait(false);
            if (overflow)
            {
                termination.KillNow();
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await IgnoreFailure(stdinTask).ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return new GitProcessResult(overflow ? null : process.ExitCode, output, overflow, error);
        }
        finally
        {
            if (!HasExited(process))
            {
                termination.KillNow();
                process.WaitForExit();
            }

            // До Dispose процесса: снятие из пула, если оно ещё идёт, дожидаемся, новое не начнётся.
            termination.Close();
        }
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(_options.GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in CommonArguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Только чтение: git не берёт index.lock ради попутного обновления индекса.
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Сообщения git — по-английски: по ним отличается «не репозиторий» от прочих сбоев.
        info.Environment.Remove("LANGUAGE");
        info.Environment["LC_ALL"] = "C";
        return info;
    }

    /// <summary>Работа с синхронным пайпом на своём фоновом потоке, чтобы не держать поток пула.</summary>
    private static Task<T> OnDedicatedThread<T>(Func<T> work, string name)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            PipeThreadStackBytes)
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return completion.Task;
    }

    private static bool WriteInput(Process process, byte[]? input)
    {
        try
        {
            if (input is { Length: > 0 })
            {
                var stream = process.StandardInput.BaseStream;
                stream.Write(input);
                stream.Flush();
            }
        }
        finally
        {
            process.StandardInput.Close();
        }

        return true;
    }

    private static string ReadError(Process process)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
        {
            var room = ErrorLimitChars - builder.Length;
            if (room > 0)
            {
                builder.Append(buffer, 0, Math.Min(room, read));
            }
        }

        return builder.ToString().Trim();
    }

    private static (byte[] Output, bool Overflow) ReadOutput(Stream stream, int? ceiling)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var collected = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, ChunkSize)) > 0)
            {
                if (ceiling is { } limit && collected.Length + read > limit)
                {
                    collected.Write(buffer, 0, (int)(limit - collected.Length));
                    return (collected.ToArray(), true);
                }

                collected.Write(buffer, 0, read);
            }

            return (collected.ToArray(), false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Процесс снят или закрыл stdin раньше, чем дочитал: ввод больше не нужен.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
