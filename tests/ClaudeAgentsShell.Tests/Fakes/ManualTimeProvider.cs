namespace ClaudeAgentsShell.Tests.Fakes;

/// <summary>
/// Управляемое время. Считает срабатывания таймеров — по этому счётчику видно,
/// тикает ли кадровый таймер вхолостую.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly object _sync = new();

    private DateTimeOffset _now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private int _callbacks;

    /// <summary>Сколько раз сработал хоть какой-нибудь таймер.</summary>
    public int TimerCallbacks => Volatile.Read(ref _callbacks);

    /// <summary>Сколько таймеров сейчас взведено. Ноль означает, что тикать нечему.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(static t => t.IsArmed);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Двигает время вперёд и выполняет таймеры, срок которых наступил.</summary>
    public void Advance(TimeSpan delta)
    {
        ManualTimer[] due;

        lock (_sync)
        {
            _now += delta;
            due = _timers.Where(t => t.IsDue(_now)).ToArray();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void OnFired() => Interlocked.Increment(ref _callbacks);

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _sync = new();
        private DateTimeOffset? _dueAt;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // Время берётся до захвата замка таймера. Иначе получается инверсия порядка
            // блокировок: Advance держит замок провайдера и просит замок таймера, а Change
            // держал бы замок таймера и просил замок провайдера — взаимная блокировка,
            // которая роняет хост тестов по таймауту.
            DateTimeOffset? dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;

            lock (_sync)
            {
                _dueAt = dueAt;
            }

            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal bool IsArmed
        {
            get
            {
                lock (_sync)
                {
                    return _dueAt is not null;
                }
            }
        }

        internal bool IsDue(DateTimeOffset now)
        {
            lock (_sync)
            {
                return _dueAt is { } due && due <= now;
            }
        }

        internal void Fire()
        {
            lock (_sync)
            {
                _dueAt = null;
            }

            owner.OnFired();
            callback(state);
        }
    }
}
