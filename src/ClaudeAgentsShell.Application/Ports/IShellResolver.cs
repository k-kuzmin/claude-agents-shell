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
    /// <exception cref="InvalidOperationException">Ни одна оболочка не найдена.</exception>
    ShellStartCommand Resolve(ShellKind preferred);

    /// <summary>Оболочки, найденные в системе. Для диалога настроек проекта.</summary>
    IReadOnlyList<ShellKind> Available { get; }
}
