using ClaudeAgentsShell.App;
using ClaudeAgentsShell.App.ViewModels;
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
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                AppComposition.ConfigureServices(services);
                provider = services.BuildServiceProvider(AppComposition.ProviderOptions);

                Assert.NotNull(provider.GetRequiredService<ShellViewModel>());
            }
            catch (Exception exception)
            {
                captured = exception;
            }
            finally
            {
                // Набор вкладок освобождается асинхронно, и ждать его надо в том же потоке:
                // контрол WebView2 принадлежит ему.
                provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Ожидание с пределом: зависшее освобождение должно давать красный тест, а не
        // висящий прогон.
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Сборка контейнера не завершилась за 30 секунд.");
        Assert.Null(captured);
    }
}
