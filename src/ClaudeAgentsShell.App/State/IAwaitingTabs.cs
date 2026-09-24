using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.State;

/// <summary>
/// То, что <see cref="AttentionCoordinator"/> знает о вкладках: какая перешла в «ждёт ввода»,
/// какая из него ушла и остался ли кто-то ждущий. Реализует полоса вкладок.
/// </summary>
/// <remarks>
/// События поднимаются на <b>гранях</b> состояния отдельной вкладки, а не на изменении
/// счётчика: одновременный уход одной вкладки и приход другой счётчик не меняет, а
/// уведомление новой вкладке всё равно положено.
/// <para>
/// К моменту события <see cref="HasAwaitingInput"/> уже учитывает переход. Все события
/// поднимаются в потоке интерфейса — вкладки объекты ViewModel.
/// </para>
/// </remarks>
public interface IAwaitingTabs
{
    /// <summary>Хотя бы одна открытая вкладка ждёт ввода.</summary>
    bool HasAwaitingInput { get; }

    /// <summary>Вкладка перешла в «ждёт ввода» (или добавлена уже ждущей).</summary>
    event EventHandler<TabViewModel>? TabBecameAwaiting;

    /// <summary>
    /// Вкладка вышла из «ждёт ввода»: сменилось состояние, либо её закрыли в ожидании —
    /// в том числе вместе с проектом.
    /// </summary>
    event EventHandler<TabViewModel>? TabLeftAwaiting;
}
