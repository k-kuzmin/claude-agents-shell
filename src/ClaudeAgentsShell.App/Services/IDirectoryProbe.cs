namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Проверка существования каталога. Ровно один метод: ViewModel нужно знать только это,
/// а <c>Directory.*</c> в ней запрещён (раздел 8 ТЗ: исчезнувший каталог блокирует запуск).
/// </summary>
public interface IDirectoryProbe
{
    /// <summary>Каталог существует и доступен.</summary>
    bool Exists(string path);
}
