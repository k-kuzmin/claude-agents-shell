using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Shells;
using Xunit;

namespace ClaudeAgentsShell.Tests.Shells;

public sealed class ShellResolverTests
{
    [Fact]
    public void Запрошенная_оболочка_выигрывает_у_порядка_регистрации()
    {
        var resolver = new ShellResolver(
        [
            Provider(ShellKind.Pwsh, found: true),
            Provider(ShellKind.WindowsPowerShell, found: true),
            Provider(ShellKind.Cmd, found: true),
        ]);

        Assert.Equal(ShellKind.Cmd, resolver.Resolve(ShellKind.Cmd).Kind);
    }

    [Fact]
    public void Отсутствие_pwsh_откатывает_на_следующую_по_порядку()
    {
        var resolver = new ShellResolver(
        [
            Provider(ShellKind.Pwsh, found: false),
            Provider(ShellKind.WindowsPowerShell, found: true),
            Provider(ShellKind.Cmd, found: true),
        ]);

        Assert.Equal(ShellKind.WindowsPowerShell, resolver.Resolve(ShellKind.Pwsh).Kind);
    }

    [Fact]
    public void Доступными_считаются_только_найденные()
    {
        var resolver = new ShellResolver(
        [
            Provider(ShellKind.Pwsh, found: false),
            Provider(ShellKind.WindowsPowerShell, found: true),
            Provider(ShellKind.Cmd, found: true),
        ]);

        Assert.Equal([ShellKind.WindowsPowerShell, ShellKind.Cmd], resolver.Available);
    }

    [Fact]
    public void Без_единой_оболочки_резолвер_говорит_об_этом_явно()
    {
        var resolver = new ShellResolver([Provider(ShellKind.Pwsh, found: false)]);

        Assert.Throws<ShellNotFoundException>(() => resolver.Resolve(ShellKind.Pwsh));
    }

    [Fact]
    public void Поиск_по_файловой_системе_не_повторяется()
    {
        var provider = new CountingProvider(ShellKind.Pwsh, found: true);
        var resolver = new ShellResolver([provider]);

        resolver.Resolve(ShellKind.Pwsh);
        resolver.Resolve(ShellKind.Pwsh);
        _ = resolver.Available;

        Assert.Equal(1, provider.Probes);
    }

    [Fact]
    public void На_windows_всегда_есть_хотя_бы_одна_реальная_оболочка()
    {
        var resolver = new ShellResolver(
        [
            new PwshShellProvider(),
            new WindowsPowerShellProvider(),
            new CmdShellProvider(),
        ]);

        var shell = resolver.Resolve(ShellKind.Pwsh);

        Assert.True(File.Exists(shell.FileName), $"Не найден файл оболочки: {shell.FileName}");
        Assert.NotEmpty(resolver.Available);
    }

    private static IShellProvider Provider(ShellKind kind, bool found) => new CountingProvider(kind, found);

    private sealed class CountingProvider(ShellKind kind, bool found) : IShellProvider
    {
        private int _probes;

        public ShellKind Kind => kind;

        public int Probes => _probes;

        public ShellStartCommand? TryResolve()
        {
            Interlocked.Increment(ref _probes);
            return found ? new ShellStartCommand(kind, $"C:\\fake\\{kind}.exe", []) : null;
        }
    }
}
