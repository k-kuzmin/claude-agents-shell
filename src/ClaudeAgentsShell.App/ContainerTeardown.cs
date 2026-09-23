using Microsoft.Extensions.DependencyInjection;

namespace ClaudeAgentsShell.App;

/// <summary>
/// Освобождение контейнера из потока интерфейса, который при этом стоит в синхронном ожидании.
/// </summary>
/// <remarks>
/// <para>
/// Контейнер освобождает службы в обратном порядке создания и ждёт асинхронные освобождения
/// с <c>ConfigureAwait(false)</c>. Стоит одной службе вернуть незавершённый
/// <see cref="ValueTask"/> — например, журналу хуков, чья фоновая запись ещё не закончилась, —
/// и всё, что освобождается после неё, освобождается уже в пуле потоков. Мост создаётся одним
/// из первых и освобождается одним из последних; из пула он отправляет освобождение контрола
/// WebView2 в диспетчер, а поток диспетчера в это время заблокирован ожиданием контейнера.
/// Итог — вечное зависание выхода: гонка, которая срабатывает лишь иногда.
/// </para>
/// <para>
/// Поэтому мост освобождается первым и прямо здесь, в своём потоке: там освобождение синхронно,
/// а повторный вызов из контейнера возвращается сразу, из любого потока.
/// </para>
/// </remarks>
internal static class ContainerTeardown
{
    /// <summary>Освобождает мост, затем контейнер. Вызывается только из потока интерфейса.</summary>
    /// <param name="services">Контейнер приложения.</param>
    /// <param name="bridge">Мост, если он успел создаться; <c>null</c> — освобождать нечего.</param>
    /// <exception cref="InvalidOperationException">Вызов не из потока моста.</exception>
    public static void DisposeFromUiThread(ServiceProvider services, WebView2TerminalBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (bridge is not null)
        {
            var release = bridge.DisposeAsync();
            if (!release.IsCompleted)
            {
                // Не из своего потока мост ждёт диспетчер — а он, возможно, ждёт нас. Лучше
                // исключение сейчас, чем зависший выход.
                throw new InvalidOperationException("Мост освобождается не из потока интерфейса.");
            }

            release.GetAwaiter().GetResult();
        }

        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
