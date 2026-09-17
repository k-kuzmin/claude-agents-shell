namespace ClaudeAgentsShell.Tests.Sessions;

/// <summary>
/// Временный каталог для файловых тестов. Удаляется в <see cref="Dispose" /> при любом исходе,
/// так что упавший тест не оставляет мусора в %TEMP%.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cas-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Антивирус или ещё открытый дескриптор — уборка не должна валить зелёный тест.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
