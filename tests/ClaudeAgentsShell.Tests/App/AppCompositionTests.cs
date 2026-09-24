using ClaudeAgentsShell.App;
using ClaudeAgentsShell.App.Diff;
using ClaudeAgentsShell.App.Services.Attention;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Sessions.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Состав контейнера. Порт, добавленный в конструктор без регистрации, компилируется и не
/// роняет ни один тест на ViewModel — падает только приложение: на старте, если тип входит
/// в граф главного окна, или при первом открытии диалога, если нет. Тест строит ту же
/// коллекцию служб, что и <see cref="global::ClaudeAgentsShell.App.App"/>, и разрешает граф.
/// Список регистраций здесь не дублируется: дубль был бы зелёным при любом промахе.
/// </summary>
public sealed class AppCompositionTests
{
    [Fact]
    public void Граф_контейнера_собирается_целиком()
    {
        // ValidateOnBuild обходит каждую регистрацию и строит план создания, не вызывая
        // конструкторов: ни Dispatcher, ни разметка окна здесь не нужны, зато недостающая
        // регистрация видна для всех типов сразу, включая те, что создаются лениво.
        var services = new ServiceCollection();
        AppComposition.ConfigureServices(services);

        var provider = services.BuildServiceProvider(AppComposition.ProviderOptions);

        provider.Dispose();
    }

    [Fact]
    public void Корневая_ViewModel_и_её_граф_действительно_создаются()
    {
        // Проверка плана не вызывает конструкторов, поэтому граф ещё и создаётся целиком:
        // так видно и несобираемый тип, а не только незарегистрированный. Поток STA нужен
        // мосту (контрол WebView2) и WpfUiDispatcher (Dispatcher.CurrentDispatcher) — так же,
        // как в настоящем приложении, где контейнер строится в потоке интерфейса.
        // Главное окно не разрешается: ему нужен уже поднятый WebView2 и показ на экране.
        RunOnUiThread(
            configure: static _ => { },
            use: static provider =>
            {
                Assert.NotNull(provider.GetRequiredService<ShellViewModel>());

                // Приёмник хуков создаётся целиком: маршрут /mcp → инструмент show_diff →
                // координатор diff. Цикл в этой цепочке здесь и упал бы. Инструмент обязан
                // получить тот же координатор, что слушает панель.
                Assert.NotNull(provider.GetRequiredService<IHookListener>());
                var coordinator = provider.GetRequiredService<DiffCoordinator>();
                Assert.Same(coordinator, provider.GetRequiredService<IShowDiffHandler>());
                Assert.Same(coordinator, provider.GetRequiredService<IDiffChangeSink>());
                Assert.Contains(
                    provider.GetServices<IMcpTool>(),
                    static tool => tool is ShowDiffTool && tool.Name == "show_diff");
            });
    }

    [Fact]
    public void Координатор_сигнала_ждёт_ввода_создаётся_из_контейнера()
    {
        // На координатор никто не ссылается — App получает его явно на старте, и граф
        // главного окна его не покрывает. Фабрики IAwaitingTabs, ITabNavigation и
        // IAwaitingToasts валидатору непрозрачны, поэтому граф создаётся по-настоящему.
        // Фокус подменён: адаптеру нужен Application.Current, которого в тесте нет.
        RunOnUiThread(
            configure: static services => services.AddSingleton<IAppFocus>(new FakeAppFocus()),
            use: static provider =>
            {
                Assert.NotNull(provider.GetRequiredService<AttentionCoordinator>());

                // Порты вкладок — те же объекты, что видит окно, а не свои копии.
                var shell = provider.GetRequiredService<ShellViewModel>();
                Assert.Same(shell.Tabs, provider.GetRequiredService<IAwaitingTabs>());
                Assert.Same(shell, provider.GetRequiredService<ITabNavigation>());
            });
    }

    [Fact]
    public void Освобождение_контейнера_в_потоке_интерфейса_не_виснет_за_асинхронным_освобождением()
    {
        // Воспроизводит зависание, которое этот тест раньше ловил примерно раз в десять полных
        // прогонов: служба, созданная после моста и потому освобождаемая раньше него, завершает
        // DisposeAsync асинхронно (так делает журнал хуков, пока его фоновая запись не
        // закончилась). Контейнер продолжает освобождение уже в пуле потоков, мост оттуда
        // отправляет освобождение контрола в диспетчер, а поток интерфейса стоит в ожидании
        // контейнера. Здесь асинхронность гарантирована, а не зависит от нагрузки; без
        // ContainerTeardown тест висит каждый раз.
        RunOnUiThread(
            configure: static services => services.AddSingleton<SlowDisposable>(),
            use: static provider =>
            {
                Assert.NotNull(provider.GetRequiredService<ShellViewModel>());
                Assert.NotNull(provider.GetRequiredService<SlowDisposable>());
            });
    }

    private static void RunOnUiThread(Action<IServiceCollection> configure, Action<ServiceProvider> use)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                AppComposition.ConfigureServices(services);
                configure(services);
                provider = services.BuildServiceProvider(AppComposition.ProviderOptions);

                use(provider);
            }
            catch (Exception exception)
            {
                captured = exception;
            }
            finally
            {
                // Так же, как App.OnExit: контрол WebView2 принадлежит этому потоку, и ждать
                // освобождения контейнера здесь можно только через ContainerTeardown.
                if (provider is not null)
                {
                    ContainerTeardown.DisposeFromUiThread(
                        provider, provider.GetRequiredService<WebView2TerminalBridge>());
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Ожидание с пределом: зависшее освобождение должно давать красный тест, а не
        // висящий прогон.
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Сборка контейнера не завершилась за 30 секунд.");
        Assert.Null(captured);
    }

    /// <summary>
    /// Служба, которая создаётся после моста (зависит от него) и освобождается асинхронно.
    /// </summary>
    private sealed class SlowDisposable(ITerminalBridge bridge) : IAsyncDisposable
    {
        public ITerminalBridge Bridge { get; } = bridge;

        // Задержка, а не Task.Yield: продолжение Yield успевает выполниться в пуле раньше,
        // чем контейнер проверит IsCompletedSuccessfully, и тогда он идёт дальше синхронно —
        // та же гонка, из-за которой настоящее зависание случалось лишь иногда.
        public async ValueTask DisposeAsync() => await Task.Delay(50).ConfigureAwait(false);
    }
}
