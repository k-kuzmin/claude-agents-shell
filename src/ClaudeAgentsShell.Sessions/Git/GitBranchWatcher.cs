using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Git;

/// <summary>
/// Следит за <c>HEAD</c> через <see cref="FileSystemWatcher" />: периодического опроса нет
/// (раздел 7 CLAUDE.md). События склеиваются дебаунсером — одна операция git трогает
/// <c>.git</c> многократно, и HEAD читается уже после того, как git отпустил блокировку.
/// <para>
/// <see cref="BranchChanged" /> приходит из потока пула, а не из потока UI: подписчик
/// сам переводит его в свой диспетчер.
/// </para>
/// </summary>
public sealed class GitBranchWatcher : IGitBranchWatcher
{
    private const string HeadFilter = "HEAD";

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

        var headFile = await GitHeadLocator.FindHeadFileAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var gitDirectory = headFile is null ? null : Path.GetDirectoryName(headFile);
        if (string.IsNullOrEmpty(gitDirectory) || !Directory.Exists(gitDirectory))
        {
            // Не репозиторий или каталог исчез — слежение просто не начинается.
            return;
        }

        var branch = await _reader.ReadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

        Entry entry;
        try
        {
            entry = CreateEntry(workingDirectory, gitDirectory, branch);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (IOException)
        {
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
            return;
        }

        try
        {
            entry.Watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Unwatch(workingDirectory);
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

    private Entry CreateEntry(string workingDirectory, string gitDirectory, string? branch)
    {
        var watcher = new FileSystemWatcher(gitDirectory, HeadFilter)
        {
            // Git пишет HEAD.lock и переименовывает его поверх HEAD, поэтому одного
            // Changed мало: нужен и FileName, иначе смена ветки проходит незамеченной.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };

        return new Entry(workingDirectory, watcher, branch, _debounce, _timeProvider, RefreshAsync);
    }

    private async Task RefreshAsync(Entry entry, CancellationToken cancellationToken)
    {
        var branch = await _reader.ReadAsync(entry.WorkingDirectory, cancellationToken).ConfigureAwait(false);

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

        public Entry(
            string workingDirectory,
            FileSystemWatcher watcher,
            string? branch,
            TimeSpan debounce,
            TimeProvider timeProvider,
            Func<Entry, CancellationToken, Task> refresh)
        {
            WorkingDirectory = workingDirectory;
            Watcher = watcher;
            LastBranch = branch;
            _debouncer = new Debouncer(debounce, token => refresh(this, token), timeProvider);

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;

            // FileSystemWatcher перестаёт слать события при переполнении буфера и при исчезновении
            // каталога. Сигнал дебаунсеру превращает это в честное «ветки нет», а не в застывшее имя.
            watcher.Error += OnError;
        }

        public string WorkingDirectory { get; }

        public FileSystemWatcher Watcher { get; }

        /// <summary>Последнее сообщённое значение; читается и пишется под замком владельца.</summary>
        public string? LastBranch { get; set; }

        public void Dispose()
        {
            Watcher.Changed -= OnChanged;
            Watcher.Created -= OnChanged;
            Watcher.Deleted -= OnChanged;
            Watcher.Renamed -= OnRenamed;
            Watcher.Error -= OnError;
            Watcher.Dispose();
            _debouncer.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs args) => _debouncer.Signal();

        private void OnRenamed(object sender, RenamedEventArgs args) => _debouncer.Signal();

        private void OnError(object sender, ErrorEventArgs args) => _debouncer.Signal();
    }
}
