using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.History;

/// <summary>
/// Следит за <c>~/.claude/projects/&lt;slug&gt;</c> через <see cref="FileSystemWatcher" />
/// (раздел 5.2 ТЗ): периодического опроса нет (раздел 7 CLAUDE.md), файлы только наблюдаются.
/// <para>
/// Каталога истории ещё нет — наблюдение ставится на ближайшего существующего предка (не выше
/// каталога, содержащего <c>.claude</c>) и переключается на каталог истории, как только тот
/// появится. После каждой перестановки заново проверяется, какой уровень существует сейчас:
/// <c>CreateDirectory</c> создаёт несколько уровней разом, и о вложенных событий уже не будет.
/// </para>
/// <para>
/// События сворачиваются окном фиксированной длины от первого события пачки, а не сдвигаемым:
/// идущая сессия дописывает транскрипт непрерывно, и сдвигаемое окно не сработало бы никогда.
/// Вызовы <c>changed</c> не перекрываются; события, пришедшие во время вызова, взводят окно снова.
/// </para>
/// </summary>
public sealed class SessionHistoryWatcher : ISessionHistoryWatcher
{
    /// <summary>Окно сворачивания по умолчанию.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Сколько уровней над каталогом истории разрешено наблюдать: <c>projects</c>, <c>.claude</c>
    /// и каталог, в котором лежит <c>.claude</c>. Выше смысла нет — это уже не установка Claude Code.
    /// </summary>
    private const int AncestorLimit = 3;

    private readonly IAppDataPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;

    /// <inheritdoc cref="SessionHistoryWatcher" />
    /// <param name="paths">Каталоги приложения и Claude Code.</param>
    /// <param name="timeProvider">Источник времени для окна сворачивания.</param>
    /// <param name="debounce">Окно сворачивания; по умолчанию <see cref="DefaultDebounce" />.</param>
    public SessionHistoryWatcher(IAppDataPaths paths, TimeProvider timeProvider, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var window = debounce ?? DefaultDebounce;
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);

        _paths = paths;
        _timeProvider = timeProvider;
        _debounce = window;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Освобождение подписки дожидается уже идущего вызова <paramref name="changed" /> (кроме
    /// случая, когда оно выполняется изнутри самого вызова). Поэтому обработчик не должен
    /// синхронно ждать поток, который освобождает подписку: <c>Dispatcher.Invoke</c> внутри
    /// обработчика при освобождении из потока UI — взаимоблокировка. Нужен
    /// <c>InvokeAsync</c>/<c>BeginInvoke</c>. Исключения обработчика гасятся: он вызывается
    /// из пула потоков, и необработанное исключение там роняло бы приложение.
    /// </remarks>
    public IDisposable Watch(string workingDirectory, Action changed)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(changed);

        var target = Path.Combine(_paths.ClaudeProjects, SessionSlug.From(workingDirectory));
        return new Subscription(target, changed, _debounce, _timeProvider);
    }

    /// <summary>Наблюдение за одним каталогом истории.</summary>
    private sealed class Subscription : IDisposable
    {
        private const string TranscriptFilter = "*.jsonl";

        private readonly string _target;
        private readonly Action _changed;
        private readonly TimeSpan _debounce;
        private readonly ITimer _timer;
        private readonly object _sync = new();

        private FileSystemWatcher? _watcher;
        private bool _watchingTarget;
        private bool _armed;
        private bool _pending;
        private bool _reevaluate;
        private bool _disposed;
        private int _invokingThread;

        public Subscription(string target, Action changed, TimeSpan debounce, TimeProvider timeProvider)
        {
            _target = target;
            _changed = changed;
            _debounce = debounce;
            _timer = timeProvider.CreateTimer(
                static state => ((Subscription)state!).OnElapsed(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            // Первая расстановка идёт до того, как подписка отдана вызывающему, поэтому таймер
            // ещё не взведён и гонки с OnElapsed нет. О «появлении» при старте не сообщается:
            // вызывающий только что прочитал список сам.
            Reattach();
        }

        public void Dispose()
        {
            FileSystemWatcher? watcher;

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                // Идущий вызов дожидается: после возврата Dispose обработчик больше не работает.
                // Исключение — Dispose из самого обработчика, иначе он ждал бы сам себя.
                while (_invokingThread != 0 && _invokingThread != Environment.CurrentManagedThreadId)
                {
                    Monitor.Wait(_sync);
                }

                watcher = _watcher;
                _watcher = null;
            }

            Detach(watcher);
            _timer.Dispose();
        }

        /// <summary>
        /// Ставит наблюдение на самый глубокий существующий уровень пути к каталогу истории.
        /// Вызывается из конструктора и из <see cref="OnElapsed" />, то есть никогда параллельно.
        /// </summary>
        /// <returns><c>true</c>, если наблюдается сам каталог истории.</returns>
        private bool Reattach()
        {
            var level = DeepestExisting();

            FileSystemWatcher? created = null;
            if (level is not null)
            {
                created = TryCreate(level, level == _target);
                if (created is null && level == _target)
                {
                    // Каталог исчез между проверкой и созданием наблюдателя — ждём его у предка.
                    level = DeepestExisting();
                    created = level is null || level == _target ? null : TryCreate(level, forTarget: false);
                }
            }

            var watchingTarget = created is not null && level == _target;
            FileSystemWatcher? previous;

            lock (_sync)
            {
                if (_disposed)
                {
                    Detach(created);
                    return false;
                }

                previous = _watcher;
                _watcher = created;
                _watchingTarget = watchingTarget;
                _reevaluate = false;
            }

            Detach(previous);

            if (created is null)
            {
                // Нет ни каталога истории, ни предков в пределах лимита — наблюдать не за чем.
                return false;
            }

            // Не поднялся (каталог пропал) либо, пока ставили наблюдение у предка, путь уже
            // дорос глубже — о тех уровнях событий не будет, поэтому пересобрать на ближайшем окне.
            if (!Start(created) || (!watchingTarget && DeepestExisting() != level))
            {
                lock (_sync)
                {
                    _reevaluate = true;
                }

                Signal();
                return false;
            }

            return watchingTarget;
        }

        /// <summary>Самый глубокий существующий уровень от каталога истории вверх, в пределах лимита.</summary>
        private string? DeepestExisting()
        {
            var current = _target;
            for (var depth = 0; depth <= AncestorLimit && !string.IsNullOrEmpty(current); depth++)
            {
                try
                {
                    if (Directory.Exists(current))
                    {
                        return current;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        private FileSystemWatcher? TryCreate(string directory, bool forTarget)
        {
            try
            {
                var watcher = forTarget
                    ? new FileSystemWatcher(directory, TranscriptFilter)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    }
                    : new FileSystemWatcher(directory)
                    {
                        // У предка важно только появление следующего уровня пути.
                        NotifyFilter = NotifyFilters.DirectoryName,
                    };

                watcher.IncludeSubdirectories = false;
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                return watcher;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool Start(FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = true;
                return true;
            }
            catch (Exception exception) when (exception is IOException
                                                  or ArgumentException
                                                  or UnauthorizedAccessException
                                                  or ObjectDisposedException
                                                  or FileNotFoundException)
            {
                return false;
            }
        }

        private void Detach(FileSystemWatcher? watcher)
        {
            if (watcher is null)
            {
                return;
            }

            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs args) => OnEvent(sender);

        private void OnRenamed(object sender, RenamedEventArgs args) => OnEvent(sender);

        private void OnEvent(object sender)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(sender, _watcher))
                {
                    // Запоздалое событие снятого наблюдателя.
                    return;
                }

                if (!_watchingTarget)
                {
                    // У предка появился подкаталог — возможно, следующий уровень пути.
                    _reevaluate = true;
                }
            }

            Signal();
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            // Переполнение буфера или пропажа каталога: что именно потеряно, неизвестно,
            // поэтому список перечитывается, а наблюдение пересобирается.
            lock (_sync)
            {
                if (!ReferenceEquals(sender, _watcher))
                {
                    return;
                }

                _reevaluate = true;
            }

            Signal();
        }

        /// <summary>Взводит окно, если оно ещё не взведено: окно отсчитывается от первого события.</summary>
        private void Signal()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (_invokingThread != 0)
                {
                    // Идёт вызов — окно взведётся по его окончании.
                    _pending = true;
                    return;
                }

                if (_armed)
                {
                    return;
                }

                _armed = true;
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnElapsed()
        {
            bool reevaluate;
            bool wasWatchingTarget;

            lock (_sync)
            {
                _armed = false;
                if (_disposed || _invokingThread != 0)
                {
                    return;
                }

                _invokingThread = Environment.CurrentManagedThreadId;
                reevaluate = _reevaluate;
                wasWatchingTarget = _watchingTarget;
            }

            try
            {
                var watchingTarget = reevaluate ? Reattach() : wasWatchingTarget;

                // Пока каталога истории нет и не было, сообщать нечего: список как был пустым,
                // так и остался. Появился, пропал или изменился — вызывающий перечитывает.
                if (wasWatchingTarget || watchingTarget)
                {
                    Invoke();
                }
            }
            finally
            {
                bool rearm;
                lock (_sync)
                {
                    _invokingThread = 0;
                    Monitor.PulseAll(_sync);

                    rearm = _pending && !_disposed;
                    _pending = false;
                }

                if (rearm)
                {
                    Signal();
                }
            }
        }

        private void Invoke()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                _changed();
            }
            catch (Exception)
            {
                // Обработчик вызывается из пула потоков: исключение там роняло бы приложение,
                // а история — справочный список, её сбой не стоит процесса.
            }
        }
    }
}
