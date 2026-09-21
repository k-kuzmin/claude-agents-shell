using ClaudeAgentsShell.App;
using ClaudeAgentsShell.Terminal;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Скрипт настроек страницы терминалов. Источник правды один — <see cref="TerminalOptions"/>,
/// в app.js этих чисел нет, поэтому проверяем, что каждое из них до страницы доезжает.
/// </summary>
public sealed class TerminalConfigScriptTests
{
    [Fact]
    public void Build_CarriesEveryOption()
    {
        var options = new TerminalOptions
        {
            Scrollback = 5000,
            ExitedScrollback = 500,
            ResizeDebounce = TimeSpan.FromMilliseconds(80)
        };

        string script = WebView2TerminalBridge.BuildConfigScript(options);

        Assert.Equal(
            "window.__terminalConfig = { scrollback: 5000, exitedScrollback: 500, resizeDebounceMs: 80 };",
            script);
    }

    [Fact]
    public void Build_ExitedScrollbackFollowsOptions()
    {
        // Число живёт в C# и нигде больше: подмена настройки обязана дойти до страницы.
        var options = new TerminalOptions { ExitedScrollback = 120 };

        Assert.Contains("exitedScrollback: 120", WebView2TerminalBridge.BuildConfigScript(options), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZeroScrollbackStaysZero()
    {
        // Ноль — легальная настройка (раздел 3.5 ТЗ). Страница сама возьмёт меньшее из двух,
        // но подменять ноль здесь нельзя: скрипт обязан отдавать ровно то, что задано.
        var options = new TerminalOptions { Scrollback = 0, ExitedScrollback = 500 };

        string script = WebView2TerminalBridge.BuildConfigScript(options);

        Assert.Contains("scrollback: 0,", script, StringComparison.Ordinal);
        Assert.Contains("exitedScrollback: 500", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_KeepDeadTabCheaperThanLive()
    {
        // Если умолчания сравняются, урезание мёртвой вкладки перестанет что-либо освобождать.
        var defaults = new TerminalOptions();

        Assert.True(defaults.ExitedScrollback > 0);
        Assert.True(defaults.ExitedScrollback < defaults.Scrollback);
    }
}
