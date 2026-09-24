using ClaudeAgentsShell.App.Services.Attention;
using ClaudeAgentsShell.Domain;
using Microsoft.Toolkit.Uwp.Notifications;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Разметка уведомления «ждёт ввода» и разбор его аргумента. Показ уведомления требует
/// WinRT и здесь не проверяется; зато сборка разметки — единственное место, где Toolkit
/// бросает на неверной комбинации аргументов, — и разбор чужих строк проверяются целиком.
/// </summary>
public sealed class AttentionArgsTests
{
    [Theory]
    [InlineData("t1a2b3c4d5e6f")]
    [InlineData("t;1")]
    [InlineData("t=1")]
    [InlineData("t%1")]
    [InlineData("вкладка")]
    public void Идентификатор_вкладки_переживает_кодирование_и_разбор(string value)
    {
        var tab = new TerminalId(value);
        string argument = new ToastArguments().Add(AwaitingToastContent.TabKey, tab.Value).ToString();

        Assert.True(AwaitingToastContent.TryParseTab(argument, out TerminalId parsed));
        Assert.Equal(tab, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Пустой_аргумент_не_разбирается(string? argument)
    {
        Assert.False(AwaitingToastContent.TryParseTab(argument, out _));
    }

    [Fact]
    public void Аргумент_без_ключа_вкладки_не_разбирается()
    {
        string argument = new ToastArguments().Add("action", "open").ToString();

        Assert.False(AwaitingToastContent.TryParseTab(argument, out _));
    }

    [Theory]
    [InlineData("tab")]
    [InlineData("tab=")]
    [InlineData("tab=   ")]
    public void Ключ_вкладки_без_значения_не_разбирается(string argument)
    {
        Assert.False(AwaitingToastContent.TryParseTab(argument, out _));
    }

    [Theory]
    [InlineData(";;;")]
    [InlineData("=;=")]
    [InlineData("%%%")]
    [InlineData("tab=1;tab=2")]
    public void Битый_аргумент_не_бросает(string argument)
    {
        Exception? exception = Record.Exception(() => AwaitingToastContent.TryParseTab(argument, out _));

        Assert.Null(exception);
    }

    [Fact]
    public void Лишние_ключи_не_мешают_разбору()
    {
        string argument = new ToastArguments()
            .Add("action", "open")
            .Add(AwaitingToastContent.TabKey, "t1")
            .Add("from", "future")
            .ToString();

        Assert.True(AwaitingToastContent.TryParseTab(argument, out TerminalId parsed));
        Assert.Equal(new TerminalId("t1"), parsed);
    }

    [Fact]
    public void Разметка_собирается_и_несёт_вкладку_в_теле_и_кнопке()
    {
        var toast = new AwaitingToast(new TerminalId("t42"), "проект · сессия");

        ToastContent content = AwaitingToastContent.Build(toast).GetToastContent();
        string xml = content.GetContent();

        Assert.Contains("проект · сессия", xml);
        Assert.Contains(AwaitingToastContent.Body, xml);
        Assert.Contains(AwaitingToastContent.OpenButton, xml);

        Assert.True(AwaitingToastContent.TryParseTab(content.Launch, out TerminalId fromBody));
        Assert.Equal(toast.Tab, fromBody);

        ToastButton button = Assert.IsType<ToastButton>(Assert.Single(((ToastActionsCustom)content.Actions).Buttons));
        Assert.Equal(ToastActivationType.Foreground, button.ActivationType);
        Assert.True(AwaitingToastContent.TryParseTab(button.Arguments, out TerminalId fromButton));
        Assert.Equal(toast.Tab, fromButton);
    }

    [Theory]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "-ToastActivated")]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "-ToastActivated", "-Embedding")]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "-embedding", "-toastactivated")]
    public void Запуск_из_уведомления_узнаётся_по_ключу(params string[] args)
    {
        Assert.True(AwaitingToastContent.IsToastActivationLaunch(args));
    }

    [Theory]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe")]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "-Embedding")]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "ToastActivated")]
    [InlineData(@"C:\app\ClaudeAgentsShell.exe", "-ToastActivatedX")]
    public void Обычный_запуск_не_считается_запуском_из_уведомления(params string[] args)
    {
        Assert.False(AwaitingToastContent.IsToastActivationLaunch(args));
    }

    [Fact]
    public void Пустые_аргументы_не_считаются_запуском_из_уведомления()
    {
        Assert.False(AwaitingToastContent.IsToastActivationLaunch(null));
        Assert.False(AwaitingToastContent.IsToastActivationLaunch([]));
    }
}
