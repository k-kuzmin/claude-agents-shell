namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Активно ли приложение целиком. Именно приложение, а не главное окно: пока открыт
/// собственный диалог (настройки проекта, история), главное окно неактивно, но человек
/// внутри приложения — мигать и показывать уведомления в этот момент нельзя.
/// </summary>
public interface IAppFocus
{
    /// <summary>Одно из окон приложения сейчас на переднем плане.</summary>
    bool IsActive { get; }

    /// <summary>Приложение получило активацию. Поднимается в потоке интерфейса.</summary>
    event EventHandler? Activated;
}
