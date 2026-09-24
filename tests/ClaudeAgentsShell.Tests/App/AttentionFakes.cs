using ClaudeAgentsShell.App.Services.Attention;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Фокус приложения, которым управляет тест.</summary>
internal sealed class FakeAppFocus : IAppFocus
{
    public bool IsActive { get; set; }

    public event EventHandler? Activated;

    /// <summary>Пользователь переключился в приложение.</summary>
    public void Activate()
    {
        IsActive = true;
        Activated?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Считает запросы и снятия мигания.</summary>
internal sealed class FakeTaskbarAttention : ITaskbarAttention
{
    public int Requests { get; private set; }

    public int Cancels { get; private set; }

    public void Request() => Requests++;

    public void Cancel() => Cancels++;
}

/// <summary>Записывает показанные и снятые уведомления; клик поднимает тест.</summary>
internal sealed class FakeAwaitingToasts : IAwaitingToasts
{
    public List<AwaitingToast> Shown { get; } = [];

    public List<TerminalId> Removed { get; } = [];

    public int Clears { get; private set; }

    public event EventHandler<TerminalId>? Clicked;

    public void Show(AwaitingToast toast) => Shown.Add(toast);

    public void Remove(TerminalId tab) => Removed.Add(tab);

    public void Clear() => Clears++;

    /// <summary>Пользователь нажал на уведомление вкладки.</summary>
    public void Click(TerminalId tab) => Clicked?.Invoke(this, tab);
}

/// <summary>Считает подъёмы окна.</summary>
internal sealed class FakeMainWindowReveal : IMainWindowReveal
{
    public int Reveals { get; private set; }

    public void Reveal() => Reveals++;
}

/// <summary>Переход, который всегда срывается: проверка, что сбой не уходит наружу.</summary>
internal sealed class FailingTabNavigation : ITabNavigation
{
    public Task<bool> ShowTabAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(new InvalidOperationException("мост недоступен"));
}
