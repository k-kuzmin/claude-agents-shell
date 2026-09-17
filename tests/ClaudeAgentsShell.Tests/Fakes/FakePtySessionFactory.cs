using System.Collections.Concurrent;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.Fakes;

/// <summary>Выдаёт заранее настроенные псевдоконсоли-заглушки в порядке запросов.</summary>
internal sealed class FakePtySessionFactory(TimeSpan disposeDelay = default) : IPtySessionFactory
{
    private readonly ConcurrentQueue<FakePtySession> _created = new();

    public IReadOnlyCollection<FakePtySession> Created => _created;

    public IPtySession Create(PtyStartInfo startInfo)
    {
        var session = new FakePtySession { DisposeDelay = disposeDelay };
        _created.Enqueue(session);
        return session;
    }
}

/// <summary>Резолвер-заглушка: отдаёт фиктивную команду запуска, ничего не ища в системе.</summary>
internal sealed class FakeShellResolver : IShellResolver
{
    public IReadOnlyList<ShellKind> Available => [ShellKind.WindowsPowerShell];

    public ShellStartCommand Resolve(ShellKind preferred) =>
        new(ShellKind.WindowsPowerShell, @"C:\fake\powershell.exe", ["-NoLogo"]);
}
