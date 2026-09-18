using ClaudeAgentsShell.Sessions.Launch;
using Xunit;

namespace ClaudeAgentsShell.Tests.Sessions;

public sealed class UrlLauncherTests
{
    private readonly UrlLauncher _launcher = new();

    // Успешное открытие не проверяется: тест не должен поднимать браузер на машине
    // разработчика. Проверяется то, что важно для контракта, — отказ вместо исключения
    // и отказ от всего, что не http(s).

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("developer.microsoft.com/webview2")]
    [InlineData("что-то совсем не ссылка")]
    public async Task Негодная_ссылка_даёт_отказ_а_не_исключение(string url)
    {
        Assert.False(await _launcher.OpenUrlAsync(url, CancellationToken.None));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("cmd.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    public async Task Чужая_схема_в_оболочку_не_уходит(string url)
    {
        // UseShellExecute запустил бы что угодно: и файл с диска, и произвольную
        // зарегистрированную схему. Пропускаются только http и https.
        Assert.False(await _launcher.OpenUrlAsync(url, CancellationToken.None));
    }
}
