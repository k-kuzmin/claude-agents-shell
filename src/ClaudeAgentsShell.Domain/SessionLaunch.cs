namespace ClaudeAgentsShell.Domain;

/// <summary>Как именно запускается <c>claude</c> в уже поднятой оболочке.</summary>
public abstract record SessionLaunch
{
    private SessionLaunch()
    {
    }

    /// <summary>Новая сессия: <c>claude</c> без флагов продолжения.</summary>
    public sealed record NewSession : SessionLaunch;

    /// <summary>Продолжение конкретной сессии: <c>claude --resume &lt;id&gt;</c>.</summary>
    /// <param name="SessionId">Идентификатор сессии Claude Code.</param>
    public sealed record ResumeSession(string SessionId) : SessionLaunch;

    /// <summary>Продолжение последней сессии в каталоге: <c>claude --continue</c>.</summary>
    public sealed record ContinueLast : SessionLaunch;
}
