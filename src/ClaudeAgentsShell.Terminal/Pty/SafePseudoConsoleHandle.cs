using Microsoft.Win32.SafeHandles;

namespace ClaudeAgentsShell.Terminal.Pty;

/// <summary>
/// Хэндл псевдоконсоли (<c>HPCON</c>). Освобождение — <c>ClosePseudoConsole</c>,
/// который блокируется, пока ConPTY не допишет остаток вывода в свой конец пайпа.
/// Поэтому закрывать его нужно, пока читающий цикл ещё разгребает вывод,
/// и только потом закрывать хэндлы пайпов.
/// </summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}
