using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>Создаёт псевдоконсоли. Прячет за собой P/Invoke и детали ConPTY.</summary>
public interface IPtySessionFactory
{
    /// <summary>Поднимает оболочку в новой псевдоконсоли.</summary>
    /// <exception cref="PtyStartException">Процесс или псевдоконсоль создать не удалось.</exception>
    IPtySession Create(PtyStartInfo startInfo);
}

/// <summary>Псевдоконсоль или процесс оболочки создать не удалось.</summary>
public sealed class PtyStartException : Exception
{
    /// <inheritdoc cref="PtyStartException" />
    public PtyStartException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="PtyStartException" />
    public PtyStartException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
