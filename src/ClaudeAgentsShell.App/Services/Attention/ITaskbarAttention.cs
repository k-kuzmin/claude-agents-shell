namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Мигание кнопки главного окна в панели задач. Таймер мигания — системный,
/// своего опроса нет.
/// </summary>
public interface ITaskbarAttention
{
    /// <summary>Начать мигание заново, до активации приложения.</summary>
    void Request();

    /// <summary>Снять мигание и подсветку кнопки полностью.</summary>
    void Cancel();
}
