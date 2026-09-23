using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>История по рабочим каталогам без файловой системы: у каждого проекта своя.</summary>
internal sealed class ScriptedHistoryReader : ISessionHistoryReader
{
    private readonly Dictionary<string, IReadOnlyList<SessionSummary>> _byDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _reads;

    /// <summary>Сколько раз читался список.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Задаёт историю каталога; порядок неважен — сортирует окно.</summary>
    public void Set(string directory, params SessionSummary[] sessions)
    {
        lock (_byDirectory)
        {
            _byDirectory[directory] = sessions;
        }
    }

    /// <summary>Следующие чтения каталога бросают исключение.</summary>
    public void Fail(string directory)
    {
        lock (_byDirectory)
        {
            _failing.Add(directory);
        }
    }

    /// <summary>Следующее чтение каталога висит, пока возвращённая задача не будет завершена.</summary>
    public TaskCompletionSource Hold(string directory)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_byDirectory)
        {
            _gates[directory] = gate;
        }

        return gate;
    }

    public async Task<IReadOnlyList<SessionSummary>> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);

        TaskCompletionSource? gate;
        IReadOnlyList<SessionSummary> sessions;
        bool failing;
        lock (_byDirectory)
        {
            _gates.Remove(workingDirectory, out gate);

            // Снимок истории берётся на момент вызова: так видно, чей результат применился.
            sessions = _byDirectory.TryGetValue(workingDirectory, out var found) ? found : [];
            failing = _failing.Contains(workingDirectory);
        }

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (failing)
        {
            throw new IOException("диск недоступен");
        }

        return sessions;
    }

    public Task<SessionSummary?> ReadOneAsync(string workingDirectory, string sessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Окну истории одна сессия не нужна.");
}

/// <summary>Наблюдатель истории, которым тест управляет вручную.</summary>
internal sealed class RecordingHistoryWatcher : ISessionHistoryWatcher
{
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>Каталоги, за которыми наблюдают сейчас.</summary>
    public IReadOnlyList<string> LiveDirectories
    {
        get
        {
            lock (_subscriptions)
            {
                return _subscriptions.Where(static s => !s.IsDisposed).Select(static s => s.Directory).ToArray();
            }
        }
    }

    /// <summary>Сколько подписок выдано за всё время.</summary>
    public int TotalSubscriptions
    {
        get
        {
            lock (_subscriptions)
            {
                return _subscriptions.Count;
            }
        }
    }

    public IDisposable Watch(string workingDirectory, Action changed)
    {
        var subscription = new Subscription(workingDirectory, changed);
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Сообщает об изменении истории каталога — как FileSystemWatcher из пула.</summary>
    public void Fire(string directory)
    {
        Subscription[] live;
        lock (_subscriptions)
        {
            live = _subscriptions
                .Where(s => !s.IsDisposed && string.Equals(s.Directory, directory, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var subscription in live)
        {
            subscription.Changed();
        }
    }

    private sealed class Subscription(string directory, Action changed) : IDisposable
    {
        public string Directory { get; } = directory;

        public Action Changed { get; } = changed;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}

/// <summary>Часы с заданным «сейчас» и часовым поясом: даты строк не зависят от машины.</summary>
internal sealed class HistoryClock(DateTimeOffset nowUtc, TimeZoneInfo zone) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => nowUtc;

    public override TimeZoneInfo LocalTimeZone => zone;
}
