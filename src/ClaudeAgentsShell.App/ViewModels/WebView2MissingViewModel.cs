using System.Text;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Разговор с пользователем, у которого не установлен движок страницы терминалов
/// (раздел 8 ТЗ): что не нашлось, почему без этого никак и куда идти за установщиком.
/// </summary>
/// <remarks>
/// Стек-трейса здесь нет намеренно: пользователю он ничего не объясняет. Техническая
/// подробность при этом не прячется совсем — <see cref="Details"/> отдаёт цепочку причин
/// типами и сообщениями, её видно по кнопке и можно выделить и скопировать в отчёт об ошибке.
/// </remarks>
public sealed class WebView2MissingViewModel : ObservableObject
{
    private const string ShowDetailsText = "Показать подробности";
    private const string HideDetailsText = "Скрыть подробности";

    /// <summary>Длиннее этого цепочка причин не разворачивается: в окно она всё равно не влезет.</summary>
    private const int MaxCauseDepth = 8;

    private readonly IUrlLauncher _urlLauncher;

    private bool _areDetailsVisible;
    private bool _isInstallerLinkFailed;

    /// <inheritdoc cref="WebView2MissingViewModel" />
    /// <param name="failure">Сбой, с которым не поднялась страница терминалов.</param>
    /// <param name="urlLauncher">Порт открытия ссылки: <c>Process.*</c> во ViewModel запрещён.</param>
    public WebView2MissingViewModel(Exception failure, IUrlLauncher urlLauncher)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(urlLauncher);

        _urlLauncher = urlLauncher;

        Message = failure.Message;
        Details = DescribeCauses(failure);

        OpenInstallerCommand = new AsyncRelayCommand(
            _ => OpenInstallerAsync(CancellationToken.None),
            onError: _ => IsInstallerLinkFailed = true);

        ToggleDetailsCommand = new RelayCommand(_ => AreDetailsVisible = !AreDetailsVisible);
    }

    /// <summary>Что именно не нашлось — человеческим языком, из сообщения самого сбоя.</summary>
    public string Message { get; }

    /// <summary>
    /// Цепочка причин: тип и сообщение на каждом уровне, без стек-трейса.
    /// Ровно то, что имеет смысл приложить к отчёту об ошибке.
    /// </summary>
    public string Details { get; }

    /// <summary>Ссылка на установщик. Показывается текстом, чтобы её можно было перенести руками.</summary>
    public string InstallerUrl => WebView2Failure.InstallerUrl;

    /// <summary>Открывает страницу установщика в браузере.</summary>
    public AsyncRelayCommand OpenInstallerCommand { get; }

    /// <summary>Разворачивает и сворачивает блок подробностей.</summary>
    public RelayCommand ToggleDetailsCommand { get; }

    /// <summary>Блок подробностей раскрыт. По умолчанию — нет: сначала объяснение, потом детали.</summary>
    public bool AreDetailsVisible
    {
        get => _areDetailsVisible;
        private set
        {
            if (SetProperty(ref _areDetailsVisible, value))
            {
                Raise(nameof(DetailsToggleText));
            }
        }
    }

    /// <summary>Надпись на кнопке подробностей.</summary>
    public string DetailsToggleText => AreDetailsVisible ? HideDetailsText : ShowDetailsText;

    /// <summary>
    /// Браузер не открылся. Тогда окно показывает ссылку текстом и просит скопировать её
    /// руками: тупик без выхода — худшее, чем может закончиться сообщение об ошибке.
    /// </summary>
    public bool IsInstallerLinkFailed
    {
        get => _isInstallerLinkFailed;
        private set => SetProperty(ref _isInstallerLinkFailed, value);
    }

    /// <summary>
    /// Открывает страницу установщика. Метод отдельно от команды, чтобы его проверял тест,
    /// а не гонка между <c>Execute</c> и утверждением.
    /// </summary>
    public async Task OpenInstallerAsync(CancellationToken cancellationToken)
    {
        bool opened = await _urlLauncher.OpenUrlAsync(InstallerUrl, cancellationToken).ConfigureAwait(true);
        IsInstallerLinkFailed = !opened;
    }

    /// <summary>
    /// Разворачивает цепочку <see cref="Exception.InnerException"/> в текст: тип и сообщение
    /// на уровень. Стек-трейс не берётся — пользователю он не говорит ничего, а сообщение
    /// исходного исключения (например, от самого WebView2) говорит многое.
    /// </summary>
    private static string DescribeCauses(Exception failure)
    {
        var builder = new StringBuilder();
        Exception? cause = failure;

        for (int depth = 0; cause is not null && depth < MaxCauseDepth; depth++, cause = cause.InnerException)
        {
            if (depth > 0)
            {
                builder.Append(Environment.NewLine).Append("↳ ");
            }

            builder
                .Append(cause.GetType().FullName)
                .Append(": ")
                .Append(cause.Message);
        }

        return builder.ToString();
    }
}
