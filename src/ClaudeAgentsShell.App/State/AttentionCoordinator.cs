using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.Services.Attention;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Сигнал «ждёт ввода» наружу, пока человек не в приложении: мигание кнопки в панели задач
/// и системное уведомление на каждую вкладку, перешедшую в ожидание. Клик по уведомлению
/// поднимает окно и открывает вкладку.
/// </summary>
/// <remarks>
/// Работает только на событиях — граней вкладок, активации приложения и клика по
/// уведомлению; ни опроса, ни таймеров (раздел 7 CLAUDE.md). Мигание перезапускается на
/// каждый новый переход вкладки в ожидание, а активация приложения снимает всё разом,
/// сколько бы вкладок ни ждало.
/// <para>
/// Все события приходят в потоке интерфейса — так обещают порты, — поэтому синхронизации
/// нет. Ссылок на вкладки координатор не держит: всё нужное берётся из аргумента события.
/// </para>
/// </remarks>
public sealed class AttentionCoordinator : IDisposable
{
    private readonly IAwaitingTabs _tabs;
    private readonly ITabNavigation _navigation;
    private readonly IAppFocus _focus;
    private readonly ITaskbarAttention _taskbar;
    private readonly IAwaitingToasts _toasts;
    private readonly IMainWindowReveal _reveal;
    private readonly IUserPrompt _prompt;

    // Отменяет переход по клику, если окно закрывается, пока он идёт. Не освобождается:
    // продолжение перехода может коснуться токена уже после Dispose, а ресурсов у источника
    // без таймера нет.
    private readonly CancellationTokenSource _lifetime = new();

    private bool _disposed;

    /// <inheritdoc cref="AttentionCoordinator" />
    public AttentionCoordinator(
        IAwaitingTabs tabs,
        ITabNavigation navigation,
        IAppFocus focus,
        ITaskbarAttention taskbar,
        IAwaitingToasts toasts,
        IMainWindowReveal reveal,
        IUserPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(taskbar);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(reveal);
        ArgumentNullException.ThrowIfNull(prompt);

        _tabs = tabs;
        _navigation = navigation;
        _focus = focus;
        _taskbar = taskbar;
        _toasts = toasts;
        _reveal = reveal;
        _prompt = prompt;

        _tabs.TabBecameAwaiting += OnTabBecameAwaiting;
        _tabs.TabLeftAwaiting += OnTabLeftAwaiting;
        _focus.Activated += OnAppActivated;
        _toasts.Clicked += OnToastClicked;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        _tabs.TabBecameAwaiting -= OnTabBecameAwaiting;
        _tabs.TabLeftAwaiting -= OnTabLeftAwaiting;
        _focus.Activated -= OnAppActivated;
        _toasts.Clicked -= OnToastClicked;
    }

    private void OnTabBecameAwaiting(object? sender, TabViewModel tab)
    {
        // Человек в приложении — счётчик «N ждёт ввода» он и так видит.
        if (_focus.IsActive)
        {
            return;
        }

        _taskbar.Request();
        _toasts.Show(new AwaitingToast(tab.TerminalId, tab.Title));
    }

    private void OnTabLeftAwaiting(object? sender, TabViewModel tab)
    {
        // Уведомление о том, что вкладка ждёт, больше не правда — снимается при любом фокусе.
        _toasts.Remove(tab.TerminalId);

        // Счётчик полосы к этому моменту уже без ушедшей вкладки.
        if (!_tabs.HasAwaitingInput && !_focus.IsActive)
        {
            _taskbar.Cancel();
        }
    }

    private void OnAppActivated(object? sender, EventArgs e)
    {
        _taskbar.Cancel();
        _toasts.Clear();
    }

    // async void допустим только в обработчиках событий — это он и есть. Наружу не уходит
    // ничего: сбой перехода показывается пользователю, а не роняет процесс.
    private async void OnToastClicked(object? sender, TerminalId terminalId)
    {
        var cancellationToken = _lifetime.Token;

        try
        {
            // Окно поднимается в любом случае — и тогда, когда вкладки уже нет.
            _reveal.Reveal();
            await _navigation.ShowTabAsync(terminalId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Приложение закрывается — переходить уже некуда.
        }
        catch (Exception exception)
        {
            _prompt.ShowError("Ошибка", exception.GetBaseException().Message);
        }
    }
}
