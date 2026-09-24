using System.Windows;
using WpfApplication = System.Windows.Application;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Активность приложения по событиям <see cref="WpfApplication.Activated"/> и
/// <see cref="WpfApplication.Deactivated"/>. Слушается именно приложение, а не окно:
/// переход фокуса из главного окна в собственный диалог WPF не поднимает
/// <c>Application.Deactivated</c>, поэтому такой момент не считается «не в фокусе».
/// </summary>
public sealed class WpfAppFocus : IAppFocus, IDisposable
{
    private readonly WpfApplication _application;
    private bool _isActive;
    private bool _disposed;

    /// <param name="application">Приложение WPF; конструктор вызывается в потоке интерфейса.</param>
    public WpfAppFocus(WpfApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
        _isActive = application.Windows.Cast<Window>().Any(window => window.IsActive);

        _application.Activated += OnActivated;
        _application.Deactivated += OnDeactivated;
    }

    /// <inheritdoc />
    public bool IsActive => _isActive;

    /// <inheritdoc />
    public event EventHandler? Activated;

    /// <summary>Отписывается от событий приложения.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _application.Activated -= OnActivated;
        _application.Deactivated -= OnDeactivated;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _isActive = true;
        Activated?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeactivated(object? sender, EventArgs e) => _isActive = false;
}
