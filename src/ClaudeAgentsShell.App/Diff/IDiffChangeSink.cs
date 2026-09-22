namespace ClaudeAgentsShell.App.Diff;

/// <summary>Приёмник сигнала «агент вкладки мог поменять файлы» — для плашки «есть изменения».</summary>
public interface IDiffChangeSink
{
    /// <summary>
    /// Агент (главный или сабагент) вкладки с этим токеном закончил пачку инструментов.
    /// Вызывается в потоке интерфейса.
    /// </summary>
    void NotifyFilesChanged(string? correlationToken);
}
