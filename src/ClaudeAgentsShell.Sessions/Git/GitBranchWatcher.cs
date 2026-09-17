using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Следит за <c>HEAD</c> через <see cref="FileSystemWatcher" />: периодического опроса нет
/// (раздел 7 CLAUDE.md). События склеиваются дебаунсером — одна операция git трогает
/// <c>.git</c> многократно, и HEAD читается уже после того, как git отпустил блокировку.
/// <para>
/// Наблюдатель восстанавливается сам. <see cref="FileSystemWatcher" /> перестаёт слать события
/// при переполнении очереди и при исчезновении каталога; по событию <c>Error</c> он
/// пересоздаётся — тоже без опроса. Если каталог git пропал совсем, сообщается отсутствие ветки
/// и слежение снимается, чтобы повторный <see cref="WatchAsync" /> смог поднять его заново.
/// </para>
/// <para>
/// <see cref="BranchChanged" /> приходит из потока пула, а не из потока UI: подписчик
/// сам переводит его в свой диспетчер.
/// </para>
/// </summary>
public sealed class GitBranchWatcher : IGitBranchWatcher
{
    private const string HeadFilter = "HEAD";

    private const NotifyFilters HeadNotifyFilters =
        // Git пишет HEAD.lock и переименовывает его поверх HEAD, поэтому одного
        // LastWrite мало: нужен и FileName, иначе смена ветки проходит незамеченной.
        NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;

    private readonly IGitBranchReader _reader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private bool _disposed;

    /// <inheritdoc cref="GitBranchWatcher" />
    public GitBranchWatcher(IGitBranchReader reader, SessionsOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reader = reader;
        _timeProvider = timeProvider;
        _debounce = options.GitBranchDebounce;
    }

    /// <inheritdoc />
    public event EventHandler<GitBranchChangedEventArgs>? BranchChanged;

    /// <inheritdoc />
    public async Task WatchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Normalize(workingDirectory);
        if (key is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.ContainsKey(key))
            {
                return;
            }
        }

        var gitDirectory = await FindGitDirectoryAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (gitDirectory is null)
        {
            // Не репозиторий или каталог исчез — слежение просто не начинается.
            return;
        }

        var branch = await _reader.ReadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var entry = new Entry(key, workingDirectory, branch, _debounce, _timeProvider, RefreshAsync);

        if (!TryAttachWatcher(entry, gitDirectory))
        {
            entry.Dispose();
            return;
        }

        var added = false;
        lock (_sync)
        {
            if (!_disposed)
            {
                added = _entries.TryAdd(key, entry);
            }
        }

        if (!added)
        {
            entry.Dispose();
        }
    }

    /// <inheritdoc />
    public void Unwatch(string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var key = Normalize(workingDirectory);
        if (key is null)
        {
            return;
        }

        Entry? entry;
        lock (_sync)
        {
            if (!_entries.Remove(key, out entry))
            {
                return;
            }
        }

        entry.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Entry[] entries;

        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Пересобирает слежение и сообщает ветку. Вызывается только из дебаунсера, поэтому
    /// файловые операции здесь идут вне замка.
    /// </summary>
    private async Task RefreshAsync(Entry entry, CancellationToken cancellationToken)
    {
        var gitDirectory = await FindGitDirectoryAsync(entry.WorkingDirectory, cancellationToken).ConfigureAwait(false);

        if (gitDirectory is null)
        {
            // Каталог исчез: ветки нет, а запись снимается — иначе повторный WatchAsync
            // увидел бы живую запись с мёртвым наблюдателем и поле ветки замёрзло бы навсегда.
            Report(entry, null, cancellationToken);
            Drop(entry);
            return;
        }

        if (entry.NeedsWatcher(gitDirectory) && !TryAttachWatcher(entry, gitDirectory))
        {
            Report(entry, null, cancellationToken);
            Drop(entry);
            return;
        }

        var branch = await _reader.ReadAsync(entry.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        Report(entry, branch, cancellationToken);
    }

    private void Report(Entry entry, string? branch, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_sync)
        {
            if (string.Equals(entry.LastBranch, branch, StringComparison.Ordinal))
            {
                return;
            }

            entry.LastBranch = branch;
        }

        BranchChanged?.Invoke(this, new GitBranchChangedEventArgs(entry.WorkingDirectory, branch));
    }

    private void Drop(Entry entry)
    {
        var removed = false;
        lock (_sync)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                removed = _entries.Remove(entry.Key);
            }
        }

        if (removed)
        {
            entry.Dispose();
        }
    }

    private static bool TryAttachWatcher(Entry entry, string gitDirectory)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(gitDirectory, HeadFilter)
            {
                NotifyFilter = HeadNotifyFilters,
                IncludeSubdirectories = false,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }

        return entry.Attach(watcher, gitDirectory);
    }

    /// <summary>Каталог с <c>HEAD</c> либо <c>null</c>, если его нет.</summary>
    private static async Task<string?> FindGitDirectoryAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var headFile = await GitHeadLocator.FindHeadFileAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var gitDirectory = headFile is null ? null : Path.GetDirectoryName(headFile);

        return !string.IsNullOrEmpty(gitDirectory) && Directory.Exists(gitDirectory) ? gitDirectory : null;
    }

    private static string? Normalize(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly Debouncer _debouncer;
        private readonly object _watcherSync = new();

        private FileSystemWatcher? _watcher;
        private string? _watchedDirectory;
        private bool _broken;
        private bool _disposed;

        public Entry(
            string key,
            string workingDirectory,
            string? branch,
            TimeSpan debounce,
            TimeProvider timeProvider,
            Func<Entry, CancellationToken, Task> refresh)
        {
            Key = key;
            WorkingDirectory = workingDirectory;
            LastBranch = branch;
            _debouncer = new Debouncer(debounce, token => refresh(this, token), timeProvider);
        }

        /// <summary>Нормализованный путь: под ним запись лежит в словаре наблюдателя.</summary>
        public string Key { get; }

        public string WorkingDirectory { get; }

        /// <summary>Последнее сообщённое значение; читается и пишется под замком владельца.</summary>
        public string? LastBranch { get; set; }

        /// <summary>
        /// Наблюдателя нужно создать заново: его ещё нет, он сообщил об ошибке
        /// либо каталог git переехал (так бывает у worktree).
        /// </summary>
        public bool NeedsWatcher(string gitDirectory)
        {
            lock (_watcherSync)
            {
                return _watcher is null
                    || _broken
                    || !string.Equals(_watchedDirectory, gitDirectory, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Подключает нового наблюдателя вместо прежнего.</summary>
        public bool Attach(FileSystemWatcher watcher, string gitDirectory)
        {
            FileSystemWatcher? previous;

            lock (_watcherSync)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return false;
                }

                previous = _watcher;
                _watcher = watcher;
                _watchedDirectory = gitDirectory;
                _broken = false;
            }

            Detach(previous);

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            try
            {
                watcher.EnableRaisingEvents = true;
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                lock (_watcherSync)
                {
                    _broken = true;
                }

                return false;
            }
        }

        public void Dispose()
        {
            FileSystemWatcher? watcher;

            lock (_watcherSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                watcher = _watcher;
                _watcher = null;
            }

            Detach(watcher);
            _debouncer.Dispose();
        }

        private void Detach(FileSystemWatcher? watcher)
        {
            if (watcher is null)
            {
                return;
            }

            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs args) => _debouncer.Signal();

        private void OnRenamed(object sender, RenamedEventArgs args) => _debouncer.Signal();

        private void OnError(object sender, ErrorEventArgs args)
        {
            // Переполнение очереди или исчезновение каталога: событий отсюда больше не будет,
            // поэтому наблюдатель помечается сломанным и пересоздаётся на ближайшем обновлении.
            lock (_watcherSync)
            {
                _broken = true;
            }

            _debouncer.Signal();
        }
    }
}
