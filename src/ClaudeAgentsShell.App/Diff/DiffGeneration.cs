using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Одно построение панели вкладки: запрос, оглавление, отмена и ограничитель файловых
/// запросов. Новый запрос оглавления заводит новое поколение и списывает прежнее — вместе
/// с его отменой списываются и все файловые запросы, начатые от прежнего оглавления.
/// </summary>
/// <remarks>
/// Ресурсы (<see cref="CancellationTokenSource"/>, <see cref="SemaphoreSlim"/>) освобождаются,
/// когда поколение списано <b>и</b> ни одна задача им больше не пользуется: списание приходит
/// из потока интерфейса, а задачи ещё могут стоять в очереди ограничителя.
/// Поля <see cref="Index"/>, <see cref="StaleMarked"/>, <see cref="ChangedWhileBuilding"/> и <see cref="SkipCallerBatch"/>
/// меняются только под замком координатора.
/// </remarks>
internal sealed class DiffGeneration
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _fileGate;
    private int _users;
    private bool _retired;
    private bool _cancelled;
    private bool _released;

    /// <inheritdoc cref="DiffGeneration" />
    /// <param name="query">Запрос поколения.</param>
    /// <param name="maxParallelFiles">Сколько <c>git</c> на файлы может идти одновременно.</param>
    public DiffGeneration(DiffQuery query, int maxParallelFiles)
    {
        Query = query;
        _fileGate = new SemaphoreSlim(maxParallelFiles, maxParallelFiles);
    }

    /// <summary>Запрос, по которому строится панель.</summary>
    public DiffQuery Query { get; }

    /// <summary>Показанное оглавление; <c>null</c>, пока не построено или построить не вышло.</summary>
    public DiffIndex? Index { get; set; }

    /// <summary>Плашка «есть изменения» уже отправлена — до следующего обновления повторно не шлётся.</summary>
    public bool StaleMarked { get; set; }

    /// <summary>Агент менял файлы, пока строилось оглавление: плашку показать сразу после него.</summary>
    public bool ChangedWhileBuilding { get; set; }

    /// <summary>
    /// Поколение построено по вызову <c>show_diff</c>, и пачка инструментов, в которой шёл сам
    /// вызов, ещё не закончилась: её <c>PostToolBatch</c> придёт сразу после ответа и о правке
    /// файлов не говорит. Первая пачка после показа оглавления пропускается.
    /// </summary>
    public bool SkipCallerBatch { get; set; }

    /// <summary>
    /// Текущая загрузка каждого пути: повторная просьба того же пути отменяет прежнюю.
    /// Меняется только под замком координатора.
    /// </summary>
    public Dictionary<string, FileLoad> FileLoads { get; } = new(StringComparer.Ordinal);

    /// <summary>Ограничитель одновременных файловых запросов поколения.</summary>
    public SemaphoreSlim FileGate => _fileGate;

    /// <summary>
    /// Занимает поколение на время задачи и отдаёт её отмену. Списанное — <c>false</c>:
    /// работа для него уже не нужна. Каждому успешному вызову соответствует <see cref="Exit"/>.
    /// </summary>
    public bool TryEnter(out CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_retired)
            {
                cancellationToken = default;
                return false;
            }

            _users++;
            cancellationToken = _cancellation.Token;
            return true;
        }
    }

    /// <summary>Задача закончила пользоваться поколением.</summary>
    public void Exit()
    {
        lock (_sync)
        {
            _users--;
            ReleaseIfUnused();
        }
    }

    /// <summary>Списывает поколение: отменяет его работу. Повторный вызов ничего не делает.</summary>
    public void Retire()
    {
        lock (_sync)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
        }

        // Отмена — вне замка: её колбэки исполняются синхронно и могут сами дойти до Exit.
        _cancellation.Cancel();

        lock (_sync)
        {
            _cancelled = true;
            ReleaseIfUnused();
        }
    }

    private void ReleaseIfUnused()
    {
        if (!_cancelled || _users > 0 || _released)
        {
            return;
        }

        _released = true;
        _cancellation.Dispose();
        _fileGate.Dispose();
    }
}
