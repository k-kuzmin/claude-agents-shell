using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Shells;

/// <summary>
/// Выбирает оболочку перебором зарегистрированных провайдеров: сначала запрошенная,
/// затем остальные в порядке регистрации. <c>switch</c> по <see cref="ShellKind"/> здесь нет —
/// новая оболочка добавляется новой реализацией <see cref="IShellProvider"/> в DI.
/// </summary>
public sealed class ShellResolver : IShellResolver
{
    private readonly IReadOnlyList<IShellProvider> _providers;
    private readonly Dictionary<ShellKind, ShellStartCommand?> _cache = [];
    private readonly object _sync = new();

    /// <param name="providers">
    /// Провайдеры в порядке предпочтения: первый — самый желанный вариант отката.
    /// </param>
    public ShellResolver(IEnumerable<IShellProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ShellKind> Available
    {
        get
        {
            var available = new List<ShellKind>(_providers.Count);
            foreach (var provider in _providers)
            {
                if (Lookup(provider) is not null)
                {
                    available.Add(provider.Kind);
                }
            }

            return available;
        }
    }

    /// <inheritdoc />
    public ShellStartCommand Resolve(ShellKind preferred)
    {
        foreach (var provider in _providers)
        {
            if (provider.Kind == preferred && Lookup(provider) is { } exact)
            {
                return exact;
            }
        }

        foreach (var provider in _providers)
        {
            if (Lookup(provider) is { } fallback)
            {
                return fallback;
            }
        }

        throw new ShellNotFoundException(
            "Не найдена ни одна оболочка: ни pwsh, ни powershell.exe, ни cmd.exe.");
    }

    private ShellStartCommand? Lookup(IShellProvider provider)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(provider.Kind, out var cached))
            {
                return cached;
            }

            var resolved = provider.TryResolve();
            _cache[provider.Kind] = resolved;
            return resolved;
        }
    }
}
