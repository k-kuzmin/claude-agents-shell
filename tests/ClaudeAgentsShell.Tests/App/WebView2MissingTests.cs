using System.Runtime.InteropServices;
using ClaudeAgentsShell.App;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.App.Views;
using ClaudeAgentsShell.Application.Ports;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Открытие ссылки без браузера: порт зовётся, ответ порта запоминается.</summary>
internal sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<string> Opened { get; } = [];

    /// <summary>Чем отвечает следующее открытие.</summary>
    public bool Result { get; set; } = true;

    public Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        Opened.Add(url);
        return Task.FromResult(Result);
    }
}

public sealed class WebView2FailureTests
{
    [Fact]
    public void Отсутствие_рантайма_распознаётся_в_голом_исключении()
    {
        Assert.True(WebView2Failure.IsRuntimeMissing(new WebView2RuntimeNotFoundException("нет рантайма")));
    }

    [Fact]
    public void Отсутствие_рантайма_распознаётся_под_обёрткой()
    {
        // Контрол поднимает движок через задачу и отдаёт отказ обёрнутым: проверка
        // одного верхнего типа промахнулась бы, а заметить это было бы негде.
        var wrapped = new InvalidOperationException(
            "Не удалось инициализировать контрол",
            new WebView2RuntimeNotFoundException("нет рантайма"));

        Assert.True(WebView2Failure.IsRuntimeMissing(wrapped));
    }

    [Fact]
    public void Отсутствие_рантайма_распознаётся_внутри_агрегата()
    {
        var aggregate = new AggregateException(
            new IOException("посторонний сбой"),
            new WebView2RuntimeNotFoundException("нет рантайма"));

        Assert.True(WebView2Failure.IsRuntimeMissing(aggregate));
    }

    [Fact]
    public void Прочие_сбои_поднятия_страницы_за_отсутствие_рантайма_не_принимаются()
    {
        Assert.False(WebView2Failure.IsRuntimeMissing(null));
        Assert.False(WebView2Failure.IsRuntimeMissing(new IOException("диск отвалился")));
        Assert.False(WebView2Failure.IsRuntimeMissing(new COMException("движок не поднялся")));

        // Нет каталога web или не загрузилась страница — рантайм при этом установлен,
        // и ссылка на установщик пользователю не поможет.
        Assert.False(WebView2Failure.IsRuntimeMissing(
            new TerminalBridgeUnavailableException("Не найдена страница терминалов")));
    }

    [Fact]
    public void Закольцованная_цепочка_причин_не_вешает_обход()
    {
        var deepest = new IOException("низ");
        Exception chain = deepest;
        for (int level = 0; level < 64; level++)
        {
            chain = new InvalidOperationException($"уровень {level}", chain);
        }

        Assert.False(WebView2Failure.IsRuntimeMissing(chain));
    }

    [Fact]
    public void Разбор_отдаёт_отдельный_тип_только_для_отсутствующего_рантайма()
    {
        var missing = WebView2Failure.Describe(new WebView2RuntimeNotFoundException("нет рантайма"));
        Assert.IsType<TerminalRuntimeMissingException>(missing);

        var other = WebView2Failure.Describe(new COMException("движок не поднялся"));
        Assert.IsType<TerminalBridgeUnavailableException>(other);

        // Прикладной код вправе ловить базовый тип, когда разница ему не важна.
        Assert.IsAssignableFrom<TerminalBridgeUnavailableException>(missing);
    }

    [Fact]
    public void Разбор_сохраняет_исходную_причину()
    {
        var cause = new WebView2RuntimeNotFoundException("нет рантайма");

        Assert.Same(cause, WebView2Failure.Describe(cause).InnerException);
    }

    [Fact]
    public void Ссылка_на_установщик_ведёт_на_https()
    {
        Assert.True(Uri.TryCreate(WebView2Failure.InstallerUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
    }
}

public sealed class WebView2MissingViewModelTests
{
    private readonly FakeUrlLauncher _launcher = new();

    [Fact]
    public void Сообщение_берётся_из_самого_сбоя()
    {
        var model = Create(new TerminalRuntimeMissingException("Нет движка терминалов."));

        Assert.Equal("Нет движка терминалов.", model.Message);
    }

    [Fact]
    public void Подробности_разворачивают_цепочку_причин()
    {
        var model = Create(new TerminalRuntimeMissingException(
            "Нет движка терминалов.",
            new WebView2RuntimeNotFoundException("Couldn't find a compatible WebView2 Runtime")));

        Assert.Contains(nameof(TerminalRuntimeMissingException), model.Details, StringComparison.Ordinal);
        Assert.Contains("Нет движка терминалов.", model.Details, StringComparison.Ordinal);
        Assert.Contains(nameof(WebView2RuntimeNotFoundException), model.Details, StringComparison.Ordinal);
        Assert.Contains("Couldn't find a compatible WebView2 Runtime", model.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Подробности_не_содержат_стек_трейса()
    {
        // Исключение именно брошено, а не создано: у созданного StackTrace пуст,
        // и проверка проходила бы, ничего не проверяя.
        Exception thrown;
        try
        {
            throw new TerminalRuntimeMissingException(
                "Нет движка терминалов.",
                new WebView2RuntimeNotFoundException("нет рантайма"));
        }
        catch (TerminalRuntimeMissingException exception)
        {
            thrown = exception;
        }

        Assert.False(string.IsNullOrEmpty(thrown.StackTrace));

        var model = Create(thrown);

        Assert.DoesNotContain("   at ", model.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(Подробности_не_содержат_стек_трейса), model.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Подробности_сначала_скрыты_и_раскрываются_кнопкой()
    {
        var model = Create(new TerminalRuntimeMissingException("Нет движка."));

        Assert.False(model.AreDetailsVisible);
        string collapsedText = model.DetailsToggleText;

        model.ToggleDetailsCommand.Execute(null);

        Assert.True(model.AreDetailsVisible);
        Assert.NotEqual(collapsedText, model.DetailsToggleText);

        model.ToggleDetailsCommand.Execute(null);

        Assert.False(model.AreDetailsVisible);
        Assert.Equal(collapsedText, model.DetailsToggleText);
    }

    [Fact]
    public async Task Установщик_открывается_по_той_же_ссылке_что_показана_в_окне()
    {
        var model = Create(new TerminalRuntimeMissingException("Нет движка."));

        await model.OpenInstallerAsync(CancellationToken.None);

        Assert.Equal([model.InstallerUrl], _launcher.Opened);
        Assert.False(model.IsInstallerLinkFailed);
    }

    [Fact]
    public async Task Не_открывшийся_браузер_показывает_ссылку_текстом()
    {
        _launcher.Result = false;
        var model = Create(new TerminalRuntimeMissingException("Нет движка."));

        await model.OpenInstallerAsync(CancellationToken.None);

        // Тупик без выхода недопустим: ссылку должно быть видно, чтобы перенести руками.
        Assert.True(model.IsInstallerLinkFailed);
    }

    [Fact]
    public async Task Повторная_удачная_попытка_убирает_подсказку()
    {
        _launcher.Result = false;
        var model = Create(new TerminalRuntimeMissingException("Нет движка."));
        await model.OpenInstallerAsync(CancellationToken.None);

        _launcher.Result = true;
        await model.OpenInstallerAsync(CancellationToken.None);

        Assert.False(model.IsInstallerLinkFailed);
    }

    [Fact]
    public void Команда_разметки_зовёт_тот_же_метод()
    {
        var model = Create(new TerminalRuntimeMissingException("Нет движка."));

        model.OpenInstallerCommand.Execute(null);

        // Порт зовётся синхронно, до первого await, поэтому гонки здесь нет. Результат
        // открытия проверяют тесты самого метода: команда — только обёртка для разметки.
        Assert.Equal([model.InstallerUrl], _launcher.Opened);
    }

    private WebView2MissingViewModel Create(Exception failure) => new(failure, _launcher);
}

public sealed class WebView2MissingWindowTests
{
    [Fact]
    public void Разметка_окна_собирается_и_находит_все_ресурсы_темы()
    {
        // Ключи StaticResource разрешаются при загрузке разметки, а не при компиляции:
        // опечатка в любом из них бросила бы XamlParseException ровно на том пути, который
        // воспроизвести нельзя, — на машине без рантайма. Окно только создаётся, не
        // показывается: ни WebView2, ни графики здесь не поднимается.
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                var window = new WebView2MissingWindow(
                    new WebView2MissingViewModel(
                        new TerminalRuntimeMissingException("Нет движка."),
                        new FakeUrlLauncher()));

                window.Close();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
    }
}
