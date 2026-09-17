using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClaudeAgentsShell.Terminal.Pty;

/// <summary>
/// Список атрибутов потока с привязанной псевдоконсолью.
/// Создаётся двойным вызовом <c>InitializeProcThreadAttributeList</c>: первый — за размером,
/// второй — по выделенной памяти. Освобождается <c>DeleteProcThreadAttributeList</c> плюс
/// <c>FreeHGlobal</c>, и только после того, как <c>CreateProcess</c> отработал.
/// </summary>
internal sealed class ProcThreadAttributeList : IDisposable
{
    private IntPtr _buffer;
    private bool _initialized;

    private ProcThreadAttributeList(IntPtr buffer)
    {
        _buffer = buffer;
    }

    /// <summary>Указатель, который кладётся в <c>STARTUPINFOEX.lpAttributeList</c>.</summary>
    internal IntPtr Handle => _buffer;

    /// <summary>Выделяет список на один атрибут и записывает в него псевдоконсоль.</summary>
    internal static ProcThreadAttributeList CreateForPseudoConsole(SafePseudoConsoleHandle pseudoConsole)
    {
        nint size = 0;

        // Первый вызов всегда возвращает false с ERROR_INSUFFICIENT_BUFFER и заполняет размер.
        if (NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList вернул успех на запросе размера.");
        }

        int error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ErrorInsufficientBuffer || size <= 0)
        {
            throw new Win32Exception(error, "Не удалось узнать размер списка атрибутов потока.");
        }

        var list = new ProcThreadAttributeList(Marshal.AllocHGlobal(size));
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(list._buffer, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось инициализировать список атрибутов потока.");
            }

            list._initialized = true;

            // Для PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE lpValue — это сам HPCON, а не указатель на него.
            bool ok = NativeMethods.UpdateProcThreadAttribute(
                list._buffer,
                0,
                NativeMethods.ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось привязать псевдоконсоль к списку атрибутов.");
            }

            return list;
        }
        catch
        {
            list.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_buffer == IntPtr.Zero)
        {
            return;
        }

        if (_initialized)
        {
            NativeMethods.DeleteProcThreadAttributeList(_buffer);
            _initialized = false;
        }

        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
