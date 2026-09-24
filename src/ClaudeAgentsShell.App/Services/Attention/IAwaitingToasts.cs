using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Services.Attention;

/// <summary>
/// Системные уведомления «вкладка ждёт ввода». У каждой вкладки не больше одного
/// уведомления: повторный показ заменяет прежнее.
/// </summary>
public interface IAwaitingToasts
{
    /// <summary>Показать или заменить уведомление вкладки.</summary>
    void Show(AwaitingToast toast);

    /// <summary>Убрать уведомление вкладки; его нет — ничего не делает.</summary>
    void Remove(TerminalId tab);

    /// <summary>Убрать все уведомления приложения, включая Центр уведомлений.</summary>
    void Clear();

    /// <summary>
    /// Пользователь нажал на уведомление. Поднимается в потоке интерфейса. Вкладки
    /// к этому моменту может уже не быть — закрыта или убран её проект.
    /// </summary>
    event EventHandler<TerminalId>? Clicked;
}

/// <param name="Tab">Вкладка, которая ждёт ввода.</param>
/// <param name="Title">Заголовок уведомления: «проект · сессия».</param>
public sealed record AwaitingToast(TerminalId Tab, string Title);
