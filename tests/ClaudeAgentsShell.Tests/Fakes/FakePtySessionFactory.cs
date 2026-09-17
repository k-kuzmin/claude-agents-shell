using System.Collections.Concurrent;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.Fakes;

/// <summary>Выдаёт заранее настроенные псевдоконсоли-заглушки в порядке запросов.</summary>
internal sealed class FakePtySessionFactory(TimeSpan disposeDelay = default) : IPtySessionFactory
{
    private readonly ConcurrentQueue<FakePtySession> _created = new();
    private readonly ConcurrentQueue<PtyStartInfo> _startInfos = new();

    public IReadOnlyCollection<FakePtySession> Created => _created;

    /// <summary>С чем поднимали псевдоконсоли — по нему видно рабочий каталог и окружение.</summary>
    public IReadOnlyCollection<PtyStartInfo> StartInfos => _startInfos;

    /// <summary>Параметры запуска по порядку создания.</summary>
    public PtyStartInfo StartInfoAt(int index) => _startInfos.ElementAt(index);

    /// <summary>Псевдоконсоль по порядку создания: он совпадает с порядком <c>ready</c> от страницы.</summary>
    public FakePtySession At(int index) => _created.ElementAt(index);

    public IPtySession Create(PtyStartInfo startInfo)
    {
        _startInfos.Enqueue(startInfo);

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
