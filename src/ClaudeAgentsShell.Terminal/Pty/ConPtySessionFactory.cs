using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Microsoft.Win32.SafeHandles;

namespace ClaudeAgentsShell.Terminal.Pty;

/// <summary>
/// Поднимает оболочку в новой псевдоконсоли. Последовательность повторяет официальный пример
/// Microsoft <c>samples/ConPTY/EchoCon</c>: два <c>CreatePipe</c> → <c>CreatePseudoConsole</c> →
/// закрытие родительских копий PTY-концов → двойной <c>InitializeProcThreadAttributeList</c> →
/// <c>UpdateProcThreadAttribute</c> → <c>CreateProcess</c> с <c>EXTENDED_STARTUPINFO_PRESENT</c>.
/// </summary>
public sealed class ConPtySessionFactory : IPtySessionFactory
{
    /// <inheritdoc />
    public IPtySession Create(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var size = startInfo.InitialSize.IsValid ? startInfo.InitialSize : TerminalSize.Default;

        SafeFileHandle? ptyInputRead = null;
        SafeFileHandle? ptyOutputWrite = null;
        SafeFileHandle? ourInputWrite = null;
        SafeFileHandle? ourOutputRead = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        ProcThreadAttributeList? attributes = null;
        FileStream? output = null;
        FileStream? input = null;

        try
        {
            if (!NativeMethods.CreatePipe(out ptyInputRead, out ourInputWrite, IntPtr.Zero, 0) ||
                !NativeMethods.CreatePipe(out ourOutputRead, out ptyOutputWrite, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать пайпы псевдоконсоли.");
            }

            var coord = new NativeMethods.Coord { X = (short)size.Cols, Y = (short)size.Rows };
            int hr = NativeMethods.CreatePseudoConsole(coord, ptyInputRead, ptyOutputWrite, 0, out IntPtr hpc);
            if (hr != 0)
            {
                throw new PtyStartException(
                    $"CreatePseudoConsole вернул 0x{hr:X8}. ConPTY доступен начиная с Windows 10 1809.");
            }

            pseudoConsole = new SafePseudoConsoleHandle(hpc);

            // Концы пайпов, отданные ConPTY, продублированы в conhost. Родительские копии закрываются
            // сразу — иначе читающий цикл никогда не увидит EOF.
            ptyOutputWrite.Dispose();
            ptyOutputWrite = null;
            ptyInputRead.Dispose();
            ptyInputRead = null;

            attributes = ProcThreadAttributeList.CreateForPseudoConsole(pseudoConsole);

            var startupInfo = default(NativeMethods.StartupInfoEx);
            startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
            startupInfo.lpAttributeList = attributes.Handle;

            // Обязательно: без STARTF_USESTDHANDLES дочерний процесс получает стандартные
            // хэндлы родителя и пишет в чужую консоль, а в наш пайп приходит только преамбула
            // ConPTY. Сами hStdInput/hStdOutput/hStdError остаются нулевыми — их подставит
            // псевдоконсоль. Так же сделано в ConptyConnection Windows Terminal.
            startupInfo.StartupInfo.dwFlags = NativeMethods.StartfUseStdHandles;

            char[] commandLine = BuildCommandLine(startInfo.Shell);
            char[] environment = BuildEnvironmentBlock(startInfo.Environment);
            string workingDirectory = ResolveWorkingDirectory(startInfo.WorkingDirectory);

            bool created = NativeMethods.CreateProcess(
                lpApplicationName: startInfo.Shell.FileName,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                lpEnvironment: environment,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out NativeMethods.ProcessInformation processInformation);

            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Не удалось запустить оболочку «{startInfo.Shell.FileName}».");
            }

            NativeMethods.CloseHandle(processInformation.hThread);
            var process = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);

            // bufferSize: 0 — без промежуточного буфера FileStream: байты идут прямо в буфер вызывающего.
            output = new FileStream(ourOutputRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            input = new FileStream(ourInputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);

            var session = new ConPtySession(pseudoConsole, attributes, process, output, input);

            // Владение перешло сессии.
            pseudoConsole = null;
            attributes = null;
            output = null;
            input = null;
            ourOutputRead = null;
            ourInputWrite = null;

            return session;
        }
        catch (PtyStartException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PtyStartException("Не удалось поднять псевдоконсоль.", exception);
        }
        finally
        {
            input?.Dispose();
            output?.Dispose();
            attributes?.Dispose();
            pseudoConsole?.Dispose();
            ptyInputRead?.Dispose();
            ptyOutputWrite?.Dispose();
            ourInputWrite?.Dispose();
            ourOutputRead?.Dispose();
        }
    }

    private static string ResolveWorkingDirectory(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
        {
            return requested;
        }

        // Каталог проекта мог исчезнуть — сессия всё равно открывается (раздел 8 ТЗ).
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// <c>CreateProcessW</c> имеет право писать в переданную командную строку,
    /// поэтому она собирается в изменяемый буфер, а не в закреплённую managed-строку.
    /// </summary>
    private static char[] BuildCommandLine(ShellStartCommand shell)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, shell.FileName);

        foreach (string argument in shell.Arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }

        var buffer = new char[builder.Length + 1];
        builder.CopyTo(0, buffer, 0, builder.Length);
        buffer[^1] = '\0';
        return buffer;
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        bool needsQuotes = value.Length == 0 || value.AsSpan().ContainsAny(' ', '\t', '"');
        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            int backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(value[i]);
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Блок окружения: родительское окружение плюс переопределения из <see cref="PtyStartInfo"/>.
    /// Формат — <c>KEY=VALUE\0…\0\0</c> в UTF-16, имена отсортированы без учёта регистра.
    /// </summary>
    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && key.Length > 0)
            {
                variables[key] = value;
            }
        }

        foreach ((string key, string value) in overrides)
        {
            if (!string.IsNullOrEmpty(key))
            {
                variables[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var pair in variables.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');

        var buffer = new char[builder.Length];
        builder.CopyTo(0, buffer, 0, builder.Length);
        return buffer;
    }
}
