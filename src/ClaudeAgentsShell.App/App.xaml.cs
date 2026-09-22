using System.Windows;
using System.Windows.Threading;
using ClaudeAgentsShell.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Время жизни приложения: строит контейнер по <see cref="AppComposition"/>, подключает
/// глобальные обработчики исключений, показывает главное окно и детерминированно
/// освобождает контейнер на выходе.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private CrashReporter? _crashes;
    private UnhandledExceptionEventHandler? _domainHandler;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _taskHandler;
    private bool _windowShown;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        AppComposition.ConfigureServices(services);
        _services = services.BuildServiceProvider(AppComposition.ProviderOptions);

        // Обработчики подключаются до создания и показа окна: сбой в его конструкторе,
        // разметке или в поднятии WebView2 тоже должен попадать в журнал, а не убивать
        // процесс молча. Само построение контейнера выше обработчиками не прикрыто —
        // до него ещё нечем ни писать, ни показывать.
        _crashes = _services.GetRequiredService<CrashReporter>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _domainHandler = OnDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += _domainHandler;

        _taskHandler = OnUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += _taskHandler;

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _windowShown = true;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // Обработчики снимаются ПОСЛЕ освобождения, а не до него. Освобождение контейнера —
        // это приёмник хуков, наблюдатель за ветками, мост и помпы; исключение оттуда,
        // снятое заранее, не поймал бы никто — ни диспетчер (OnExit исполняется как его
        // операция), ни домен, — и процесс умер бы с дампом вместо записи в crash.log.
        // Особенно это важно после двухфазного закрытия: к этому моменту в фоне может
        // доигрываться гашение, брошенное по потолку.
        if (_services is { } services)
        {
            // К этому моменту окно уже освободило мост и псевдоконсоли; повторное освобождение
            // идемпотентно и не уходит в ожидание.
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _services = null;
        }

        DispatcherUnhandledException -= OnDispatcherUnhandledException;

        if (_taskHandler is { } taskHandler)
        {
            TaskScheduler.UnobservedTaskException -= taskHandler;
            _taskHandler = null;
        }

        // Обработчик домена снимается последним: он единственный ловит то, что прилетело
        // с чужого потока, и остаётся последней сетью, пока снимаются остальные.
        if (_domainHandler is { } domainHandler)
        {
            AppDomain.CurrentDomain.UnhandledException -= domainHandler;
            _domainHandler = null;
        }

        // Докладчик отпускается после отключения обработчиков: сбой, пришедший между
        // концом освобождения и отпиской, должен застать его на месте.
        _crashes = null;

        base.OnExit(e);
    }

    /// <summary>
    /// Исключение в потоке интерфейса. Приложение остаётся жить: потерять сессии
    /// из-за сбоя в обработчике события — худшее, что может случиться с оболочкой.
    /// </summary>
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Сбой до показа окна — особый случай: пометить его обработанным и жить дальше
        // значит оставить процесс без единого окна. Закрыться ему будет нечем
        // (ShutdownMode ждёт закрытия последнего окна), и снять его можно будет только
        // диспетчером задач. Поэтому здесь — осознанный выход. Решение принимается до
        // попытки сообщить о сбое: сбой доклада не должен превращаться в зависший процесс.
        var fatal = !_windowShown;

        // Тело целиком под catch: исключение из этого обработчика убивает процесс
        // мгновенно и без записи — ровно то, от чего обработчик и поставлен.
        try
        {
            _crashes?.Report(CrashReporter.DispatcherSource, e.Exception);
        }
        catch
        {
            // Записать сбой не удалось и сообщить о нём некому: единственное, что
            // остаётся, — не превратить его в мгновенную смерть процесса.
        }

        e.Handled = true;

        if (fatal)
        {
            Shutdown(1);
        }
    }

    /// <summary>
    /// Исключение, дошедшее до домена. Спасти процесс отсюда нельзя даже при
    /// <c>IsTerminating == false</c>: событие приходит с чужого потока, показывать окно
    /// из него нечем и незачем. Остаётся запись.
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            // ExceptionObject объявлен object: с чужого потока сюда может прилететь
            // и не Exception.
            var exception = e.ExceptionObject as Exception
                ?? new InvalidOperationException(
                    FormattableString.Invariant($"Необработанный объект: {e.ExceptionObject?.GetType().FullName ?? "null"}"));

            _crashes?.Record(CrashReporter.AppDomainSource, exception);
        }
        catch
        {
            // Процесс и так завершается; второе исключение отсюда только затрёт первое
            // в журнале Windows.
        }
    }

    /// <summary>
    /// Сбой задачи, результат которой никто не посмотрел. Пользователю показывать нечего:
    /// он об этой задаче не знает, а её сбой мог ничего не сломать. Запись и
    /// <see cref="UnobservedTaskExceptionEventArgs.SetObserved" />, чтобы сборщик мусора
    /// не поднимал это дальше.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            // SetObserved до записи: сбой журнала не должен стоить нам отметки
            // «исключение замечено» — без неё финализатор задачи поднимет его дальше.
            e.SetObserved();
            _crashes?.Record(CrashReporter.TaskSchedulerSource, e.Exception);
        }
        catch
        {
            // Обработчик зовётся из финализатора: исключение отсюда роняет процесс
            // в потоке финализации, где его уже никто не поймает.
        }
    }
}
