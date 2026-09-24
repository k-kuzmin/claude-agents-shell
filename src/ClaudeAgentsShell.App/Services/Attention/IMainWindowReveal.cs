namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>Вывести главное окно на передний план, развернув из свёрнутого.</summary>
public interface IMainWindowReveal
{
    /// <summary>Показывает и активирует главное окно.</summary>
    void Reveal();
}
