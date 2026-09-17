namespace ClaudeAgentsShell.Domain;

/// <summary>Оболочка, которая поднимается внутри ConPTY до запуска <c>claude</c>.</summary>
public enum ShellKind
{
    /// <summary>PowerShell 7+ (<c>pwsh.exe</c>). Предпочтительный вариант.</summary>
    Pwsh = 0,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>). Откат, если pwsh не установлен.</summary>
    WindowsPowerShell = 1,

    /// <summary>Классический <c>cmd.exe</c>.</summary>
    Cmd = 2,
}
