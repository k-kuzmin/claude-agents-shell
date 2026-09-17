namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Перевод работы в поток интерфейса. События портов (<c>TerminalExited</c>, смена ветки)
/// приходят из фоновых потоков — <c>FileSystemWatcher</c> и помпы вывода, — а трогать
/// коллекции ViewModel можно только из потока UI.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Выполняет действие в потоке интерфейса, не дожидаясь его завершения.</summary>
    void Post(Action action);
}
