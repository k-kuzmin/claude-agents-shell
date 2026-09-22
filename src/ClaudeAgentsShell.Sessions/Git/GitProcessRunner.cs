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
/// </summary>
public sealed class GitProcessRunner
{
    private const int ChunkSize = 64 * 1024;
    private const int ErrorLimitChars = 4 * 1024;

    private static readonly string[] CommonArguments =
    [
        "--no-pager",
        "--literal-pathspecs",
        "-c", "core.quotepath=false",

        // Встроенный fsmonitor запускает демона, который переживает команду.
        "-c", "core.fsmonitor=false",
    ];

    private readonly GitDiffOptions _options;

    /// <inheritdoc cref="GitProcessRunner" />
    public GitProcessRunner(GitDiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Запускает git и собирает вывод целиком или до потолка.</summary>
    /// <param name="workingDirectory">Каталог запуска; пути в аргументах считаются от него.</param>
    /// <param name="arguments">Аргументы после общих (<c>-c core.quotepath=false</c> и пр.).</param>
    /// <param name="standardInput">Что подать на stdin; stdin закрывается после записи в любом случае.</param>
    /// <param name="outputCeilingBytes">Потолок stdout; <c>null</c> — без потолка.</param>
    /// <param name="cancellationToken">Отмена снимает процесс вместе с потомками.</param>
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

        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments) };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new DiffUnavailableException(DiffFailure.GitNotFound, "git не найден. Установите Git и добавьте его в PATH.", exception);
        }

        var truncated = false;
        try
        {
            using var registration = cancellationToken.Register(static state => Kill((Process)state!), process);

            var stdinTask = WriteInputAsync(process, standardInput);
            var stderrTask = ReadErrorAsync(process);
            var (output, overflow) = await ReadOutputAsync(process.StandardOutput.BaseStream, outputCeilingBytes).ConfigureAwait(false);
            truncated = overflow;
            if (overflow)
            {
                Kill(process);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await IgnoreFailure(stdinTask).ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return new GitProcessResult(truncated ? null : process.ExitCode, output, truncated, error);
        }
        finally
        {
            if (!HasExited(process))
            {
                Kill(process);
                process.WaitForExit();
            }
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

    private static async Task WriteInputAsync(Process process, byte[]? input)
    {
        var stream = process.StandardInput.BaseStream;
        try
        {
            if (input is { Length: > 0 })
            {
                await stream.WriteAsync(input).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task<string> ReadErrorAsync(Process process)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = ErrorLimitChars - builder.Length;
            if (room > 0)
            {
                builder.Append(buffer, 0, Math.Min(room, read));
            }
        }

        return builder.ToString().Trim();
    }

    private static async Task<(byte[] Output, bool Overflow)> ReadOutputAsync(Stream stream, int? ceiling)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var collected = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize)).ConfigureAwait(false)) > 0)
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

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Уже завершился.
        }
        catch (Win32Exception)
        {
            // Завершается прямо сейчас — доступ к нему уже закрыт.
        }
    }
}
