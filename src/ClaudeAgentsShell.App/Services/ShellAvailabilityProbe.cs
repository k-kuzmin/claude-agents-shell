using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Реализация <see cref="IShellAvailability"/> поверх <see cref="IShellResolver"/>:
/// список берётся у того же резолвера, который потом выбирает оболочку при запуске, —
/// так диалог не может разойтись с реальностью.
/// </summary>
public sealed class ShellAvailabilityProbe : IShellAvailability
{
    private readonly IShellResolver _resolver;

    /// <inheritdoc cref="ShellAvailabilityProbe" />
    /// <param name="resolver">Резолвер оболочек слоя терминала.</param>
    public ShellAvailabilityProbe(IShellResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Свойство резолвера синхронное и на первом обращении ходит на диск, поэтому вызов
    /// уезжает в пул: диалог открывается сразу, а список оболочек доезжает следом.
    /// Повторные обращения резолвер отдаёт из своего кэша.
    /// </remarks>
    public Task<IReadOnlyList<ShellKind>> GetInstalledAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _resolver.Available, cancellationToken);
}
