using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// Переход к вкладке по идентификатору — тем же путём, что клик по счётчику «N ждёт ввода»:
/// вместе с вкладкой выбирается её проект. Реализует корневая ViewModel.
/// </summary>
/// <remarks>Вызывается в потоке интерфейса.</remarks>
public interface ITabNavigation
{
    /// <summary>
    /// Делает вкладку активной. Вкладки уже нет — закрыта или убран её проект — ничего
    /// не делает и не бросает.
    /// </summary>
    /// <returns><c>true</c>, если вкладка найдена и показана.</returns>
    Task<bool> ShowTabAsync(TerminalId terminalId, CancellationToken cancellationToken);
}
