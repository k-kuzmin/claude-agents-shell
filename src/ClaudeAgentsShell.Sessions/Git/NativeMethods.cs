using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Нативные вызовы для проверки, куда на самом деле открыт файл: .NET не даёт
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, поэтому итоговый путь сверяется по уже открытому handle.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Открыть каталог: без этого флага <c>CreateFile</c> каталоги не открывает.</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>Хватает для <c>GetFinalPathNameByHandle</c>; содержимое не читается.</summary>
    private const uint FileReadAttributes = 0x80;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c> — путь вида <c>\\?\D:\…</c>.</summary>
    private const uint FinalPathFlags = 0;

    private const int ErrorAccessDenied = 5;

    /// <summary>Итоговый путь открытого файла или каталога — после всех ссылок и точек монтирования.</summary>
    /// <exception cref="IOException">ОС не отдала путь.</exception>
    internal static string GetFinalPath(SafeHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FinalPathFlags);
            if (length == 0)
            {
                throw LastError();
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            // Не влезло: length — нужный размер с завершающим нулём.
            buffer = new char[length];
        }
    }

    /// <summary>Итоговый путь каталога — открывается только для чтения атрибутов.</summary>
    /// <exception cref="IOException">Каталог не открылся или ОС не отдала путь.</exception>
    internal static string GetFinalDirectoryPath(string directory)
    {
        using var handle = CreateFile(
            directory,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw LastError();
        }

        return GetFinalPath(handle);
    }

    /// <summary>Последняя ошибка Win32 как исключение, которое ловят вызывающие: нет прав или ввод-вывод.</summary>
    private static Exception LastError()
    {
        var error = Marshal.GetLastPInvokeError();
        var message = new System.ComponentModel.Win32Exception(error).Message;
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message)
            : new IOException(message, Marshal.GetHRForLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flags, IntPtr template);
}
