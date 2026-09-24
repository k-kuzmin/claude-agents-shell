using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Уведомления «ждёт ввода» через <see cref="ToastNotificationManagerCompat"/>. Регистрация
/// в системе происходит лениво, при первом показе: старт приложения её не оплачивает.
/// Сбой службы уведомлений (отключена, запрещена политикой, ошибка COM) не роняет
/// приложение — пишется в лог, и дальше уведомлений просто нет.
/// </summary>
/// <remarks>
/// <see cref="Show"/>, <see cref="Remove"/>, <see cref="Clear"/> и <see cref="Dispose"/>
/// вызываются из потока интерфейса. Активация уведомления приходит в фоновом потоке и
/// переводится в поток интерфейса через <see cref="IUiDispatcher"/>.
/// </remarks>
public sealed class ToolkitAwaitingToasts : IAwaitingToasts, IDisposable
{
    /// <summary>Группа всех уведомлений приложения; тег внутри группы — id вкладки.</summary>
    private const string Group = "awaiting";

    /// <summary>Источник записей в журнале сбоев.</summary>
    private const string LogSource = "Attention.Toasts";

    private readonly IUiDispatcher _dispatcher;
    private readonly ICrashLog _log;
    private CompatState _state;
    private volatile bool _disposed;

    /// <param name="dispatcher">Перевод активации уведомления в поток интерфейса.</param>
    /// <param name="log">Журнал сбоев службы уведомлений; сам не бросает.</param>
    public ToolkitAwaitingToasts(IUiDispatcher dispatcher, ICrashLog log)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <inheritdoc />
    public event EventHandler<TerminalId>? Clicked;

    /// <summary>
    /// Если процесс запущен нажатием на уведомление (exe был закрыт), инициализирует
    /// уведомления сразу, не дожидаясь первого показа: активация доставляется только после
    /// подписки на <see cref="ToastNotificationManagerCompat.OnActivated"/>, и без неё
    /// нажатие потерялось бы. Иначе ничего не делает — обычный старт регистрацию не оплачивает.
    /// Вызывать из потока интерфейса после создания главного окна.
    /// </summary>
    public void InitializeIfToastActivated()
    {
        if (_disposed || _state != CompatState.NotInitialized)
        {
            return;
        }

        try
        {
            if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
            {
                return;
            }
        }
        catch (Exception exception)
        {
            _state = CompatState.Failed;
            _log.Write(LogSource, exception);
            return;
        }

        _ = EnsureInitialized();
    }

    /// <inheritdoc />
    public void Show(AwaitingToast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        if (_disposed || !EnsureInitialized())
        {
            return;
        }

        try
        {
            string tag = toast.Tab.Value;
            AwaitingToastContent.Build(toast).Show(notification =>
            {
                // Тот же тег и группа заменяют прежнее уведомление вкладки, а не добавляют второе.
                notification.Tag = tag;
                notification.Group = Group;
            });
        }
        catch (Exception exception)
        {
            _log.Write(LogSource, exception);
        }
    }

    /// <inheritdoc />
    public void Remove(TerminalId tab)
    {
        if (_state != CompatState.Ready)
        {
            // Своих уведомлений ещё не показывали — убирать нечего.
            return;
        }

        try
        {
            ToastNotificationManagerCompat.History.Remove(tab.Value, Group);
        }
        catch (Exception exception)
        {
            _log.Write(LogSource, exception);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_state != CompatState.Ready)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception exception)
        {
            _log.Write(LogSource, exception);
        }
    }

    /// <summary>
    /// Отписывается от активации и убирает свои уведомления: оставшись в Центре уведомлений,
    /// они запускали бы exe заново уже после выхода. Регистрацию в системе не трогает.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_state != CompatState.Ready)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
        }
        catch (Exception exception)
        {
            _log.Write(LogSource, exception);
        }

        Clear();
    }

    /// <summary>
    /// Первое обращение к Compat регистрирует приложение в системе и подписывает на
    /// активацию. Сбой запоминается: повторные попытки бросали бы то же исключение
    /// инициализации типа на каждом показе.
    /// </summary>
    private bool EnsureInitialized()
    {
        switch (_state)
        {
            case CompatState.Ready:
                return true;
            case CompatState.Failed:
                return false;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _state = CompatState.Ready;
            return true;
        }
        catch (Exception exception)
        {
            _state = CompatState.Failed;
            _log.Write(LogSource, exception);
            return false;
        }
    }

    /// <summary>Приходит в фоновом потоке, в том числе во время выхода.</summary>
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        if (_disposed || !AwaitingToastContent.TryParseTab(e.Argument, out TerminalId tab))
        {
            return;
        }

        try
        {
            _dispatcher.Post(() =>
            {
                if (!_disposed)
                {
                    Clicked?.Invoke(this, tab);
                }
            });
        }
        catch (Exception exception)
        {
            // Диспетчер уже остановлен — приложение закрывается, открывать вкладку некуда.
            _log.Write(LogSource, exception);
        }
    }

    private enum CompatState
    {
        NotInitialized,
        Ready,
        Failed,
    }
}
