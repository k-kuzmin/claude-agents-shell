using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Умеет найти одну конкретную оболочку. Новая оболочка добавляется новой реализацией
/// и регистрацией в DI — без <c>switch</c> по <see cref="ShellKind"/> в прикладном коде.
/// </summary>
public interface IShellProvider
{
    /// <summary>Какую оболочку умеет находить эта реализация.</summary>
    ShellKind Kind { get; }

    /// <summary>Команда запуска, если оболочка установлена; иначе <c>null</c>.</summary>
    ShellStartCommand? TryResolve();
}

/// <summary>
/// Выбирает оболочку: сначала запрошенную, затем откат по порядку доступных.
/// Если не найдено ничего — исключение, потому что без оболочки сессии нет.
/// </summary>
public interface IShellResolver
{
    /// <summary>Разрешает запрошенную оболочку или ближайшую доступную.</summary>
    /// <exception cref="ShellNotFoundException">Ни одна оболочка не найдена.</exception>
    ShellStartCommand Resolve(ShellKind preferred);

    /// <summary>Оболочки, найденные в системе. Для диалога настроек проекта.</summary>
    IReadOnlyList<ShellKind> Available { get; }
}

/// <summary>
/// В системе не найдено ни одной оболочки.
/// Собственный тип, а не <see cref="InvalidOperationException"/>: обработчик, который ловит
/// базовый тип, заодно проглатывает дефекты потоков WPF («The calling thread cannot access
/// this object») и показывает их пользователю как «нет оболочки».
/// </summary>
public sealed class ShellNotFoundException : Exception
{
    /// <inheritdoc cref="ShellNotFoundException" />
    public ShellNotFoundException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="ShellNotFoundException" />
    public ShellNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
