using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Pty;
using ClaudeAgentsShell.Terminal.Shells;
using Xunit;

namespace ClaudeAgentsShell.Tests.Pty;

/// <summary>
/// Дымовой тест жизненного цикла ConPTY: пайпы, псевдоконсоль, список атрибутов потока,
/// запуск процесса, ресайз и детерминированное освобождение без зависания на выходе.
/// <para>
/// Сам вывод псевдоконсоли здесь не проверяется намеренно. На машине разработки
/// наблюдалось: запущенный отдельным процессом (Start-Process без -NoNewWindow) стенд
/// получает вывод дочерней оболочки через пайп полностью; тот же стенд, запущенный из
/// Git Bash или со Start-Process -NoNewWindow, получает только преамбулу ConPTY,
/// а вывод оболочки уходит в консоль запускающего окна. Разделяющий фактор установить
/// не удалось: отключение консоли родителя через FreeConsole картину не меняет
/// (в рабочей конфигурации FreeConsole тоже сообщает, что консоль была).
/// Хост xunit попадает в «плохую» конфигурацию, поэтому проверка вывода здесь зависела бы
/// от того, из-под чего запущены тесты. Вывод проверяется визуальной приёмкой приложения.
/// </para>
/// </summary>
public sealed class ConPtySessionTests
{
    [Fact]
    public async Task Ресайз_не_падает_на_живой_псевдоконсоли()
    {
        var shell = new WindowsPowerShellProvider().TryResolve();
        Assert.NotNull(shell);

        var startInfo = new PtyStartInfo(
            shell with { Arguments = ["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 5"] },
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            new TerminalSize(80, 25),
            new Dictionary<string, string> { ["TERM"] = "xterm-256color" });

        var session = new ConPtySessionFactory().Create(startInfo);
        try
        {
            session.Resize(new TerminalSize(120, 40));
            session.Resize(new TerminalSize(0, 0));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

}
