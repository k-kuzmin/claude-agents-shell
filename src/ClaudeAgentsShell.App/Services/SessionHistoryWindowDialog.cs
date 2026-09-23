using System.Windows;
using ClaudeAgentsShell.App.History;
using ClaudeAgentsShell.App.Views;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.App.Services;

/// <summary>
/// Реализация <see cref="ISessionHistoryDialog"/> через модальное окно WPF.
/// Единственное место, где приложение создаёт окно истории сессий.
/// </summary>
public sealed class SessionHistoryWindowDialog : ISessionHistoryDialog
{
    private readonly ISessionHistoryReader _reader;
    private readonly ISessionHistoryWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc cref="SessionHistoryWindowDialog" />
    /// <param name="reader">Чтение истории рабочего каталога.</param>
    /// <param name="watcher">Живое обновление списка, пока окно открыто.</param>
    /// <param name="dispatcher">Перевод результатов чтения в поток интерфейса.</param>
    /// <param name="timeProvider">Текущее время и часовой пояс для дат в строках.</param>
    public SessionHistoryWindowDialog(
        ISessionHistoryReader reader,
        ISessionHistoryWatcher watcher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reader = reader;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Задача возвращается уже завершённой: модальное окно WPF крутит свой цикл сообщений
    /// внутри <see cref="Window.ShowDialog"/>. Звать метод можно только из потока интерфейса.
    /// Подписки наблюдателя снимаются при любом закрытии — <c>Enter</c>, <c>Esc</c>, крестик,
    /// Alt+F4 или отмена токена.
    /// </remarks>
    public Task<SessionHistoryChoice?> ShowAsync(SessionHistoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var viewModel = new SessionHistoryViewModel(request, _reader, _watcher, _dispatcher, _timeProvider);
        var window = new SessionHistoryWindow(viewModel) { Owner = Owner() };

        // Окно ещё не показано: закрывать его до ShowDialog нельзя, иначе показ повиснет
        // на закрытом окне. Токен может сработать в потоке пула, поэтому закрытие
        // отправляется в поток окна.
        var shown = false;
        using var registration = cancellationToken.Register(() =>
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (shown)
                {
                    window.Close();
                }
            })));

        shown = true;
        var accepted = window.ShowDialog() == true;

        // Закрытие без выбора — штатный исход, поэтому null, а не исключение.
        return Task.FromResult(accepted ? viewModel.Result : null);
    }

    // Владелец берётся у приложения, а не хранится ссылкой: иначе порт диалога держал бы
    // главное окно живым. Тот же приём, что у ProjectSettingsWindowDialog.
    private static Window Owner() =>
        System.Windows.Application.Current?.MainWindow
        ?? throw new InvalidOperationException("Главное окно ещё не создано.");
}
