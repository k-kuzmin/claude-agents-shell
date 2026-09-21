using System.Windows;
using ClaudeAgentsShell.App;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Двухфазное закрытие окна. Гашение псевдоконсолей честно занимает секунды, и всё это
/// время закрытие отменяется — жалоба с приёмки «крестик срабатывает со второго клика»
/// была именно про это. Тесты закрывают обе стороны починки: окно уходит с экрана на
/// первой же попытке, а общий потолок не даёт невидимому процессу висеть вечно.
/// </summary>
public sealed class WindowShutdownSequenceTests
{
    // Тот самый потолок, а не его копия: разъехавшаяся копия литерала была бы зелёной
    // при любом промахе, а заодно так проверяется значение по умолчанию.
    private static readonly TimeSpan Deadline = WindowShutdownSequence.ShutdownDeadline;

    [Fact]
    public async Task Первая_попытка_отменяется_прячет_окно_и_запускает_гашение()
    {
        var recorder = new Recorder();
        var sequence = Create(recorder);

        Assert.True(sequence.HandleCloseRequest());
        Assert.Equal(1, recorder.Hidden);
        Assert.Equal(0, recorder.Closed);

        await recorder.WaitForStartAsync();
        recorder.Complete();
        await recorder.WaitForCloseAsync(sequence);

        Assert.Equal(1, recorder.Started);
        Assert.Equal(1, recorder.Closed);
        Assert.Equal(1, recorder.Hidden);
    }

    [Fact]
    public async Task Вторая_попытка_во_время_гашения_отменяется_и_не_запускает_гашение_повторно()
    {
        var recorder = new Recorder();
        var sequence = Create(recorder);

        Assert.True(sequence.HandleCloseRequest());
        await recorder.WaitForStartAsync();
        var running = sequence.Running;

        Assert.True(sequence.HandleCloseRequest());
        Assert.True(sequence.HandleCloseRequest());

        // Ни второго гашения, ни второго Hide: и то и другое посреди цепочки испортило бы
        // ей жизнь — гашение вошло бы в уже освобождённые объекты, а Hide мигнул бы окном.
        Assert.Equal(1, recorder.Started);
        Assert.Equal(1, recorder.Hidden);
        Assert.Equal(0, recorder.Closed);
        Assert.Same(running, sequence.Running);

        recorder.Complete();
        await recorder.WaitForCloseAsync(sequence);
    }

    [Fact]
    public async Task Попытка_после_завершения_гашения_не_отменяется()
    {
        var recorder = new Recorder();
        var sequence = Create(recorder);

        Assert.True(sequence.HandleCloseRequest());
        await recorder.WaitForStartAsync();
        recorder.Complete();
        await recorder.WaitForCloseAsync(sequence);

        // Окно закрывает себя само из гашения: этот вход в OnClosing — как раз тот самый,
        // и отменять его больше нельзя, иначе крестик не сработает никогда.
        Assert.False(sequence.HandleCloseRequest());
        Assert.Equal(1, recorder.Started);
        Assert.Equal(1, recorder.Hidden);
        Assert.Equal(1, recorder.Closed);
    }

    [Fact]
    public async Task Выход_за_потолок_закрывает_окно_хотя_гашение_не_кончилось()
    {
        var time = new ManualTimeProvider();
        var recorder = new Recorder();
        var sequence = new WindowShutdownSequence(
            recorder.ShutdownAsync,
            recorder.Hide,
            recorder.Close,
            time,
            new ShutdownSignal(),
            new FakeCrashLog(),
            Deadline);

        Assert.True(sequence.HandleCloseRequest());

        // Гашение стартовало и висит. Ждать надо не его начала, а именно взведённого
        // таймера: гашение зовётся раньше, чем WaitAsync успевает поставить потолок, и
        // ход часов, сделанный между этими двумя шагами, не разбудил бы никого.
        await recorder.WaitForStartAsync();
        await WaitForDeadlineTimerAsync(time);

        time.Advance(Deadline);

        await recorder.WaitForCloseAsync(sequence);

        Assert.Equal(1, recorder.Closed);
        Assert.False(recorder.IsShutdownFinished);

        // Следующая попытка проходит насквозь: окно закрывается, процесс уходит, и
        // недогашенные псевдоконсоли гибнут вместе с ним. Это принятый худший случай —
        // невидимый процесс, висящий вечно, хуже.
        Assert.False(sequence.HandleCloseRequest());

        recorder.Complete();
    }

    [Fact]
    public async Task Сбой_гашения_закрывает_окно_и_попадает_в_журнал()
    {
        var log = new FakeCrashLog();
        var recorder = new Recorder();
        var sequence = Create(recorder, new ShutdownSignal(), log);

        var failure = new InvalidOperationException("освобождение сломалось");

        Assert.True(sequence.HandleCloseRequest());
        recorder.Fail(failure);

        // Наружу сбой не выпускается: Running — поле живого объекта, ждать которое некому,
        // так что исключение задачи не подхватил бы ни UnobservedTaskException, ни
        // обработчики приложения — оно просто исчезло бы вместе с объектом.
        await sequence.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(WindowShutdownSequence.CrashSource, entry.Source);
        Assert.Same(failure, entry.Exception);

        Assert.Equal(1, recorder.Closed);
        Assert.False(sequence.HandleCloseRequest());
    }

    [Fact]
    public async Task Сбой_самого_журнала_не_мешает_закрыть_окно()
    {
        var log = new FakeCrashLog { Failure = new IOException("диск полон") };
        var recorder = new Recorder();
        var sequence = Create(recorder, new ShutdownSignal(), log);

        Assert.True(sequence.HandleCloseRequest());
        recorder.Fail(new InvalidOperationException("освобождение сломалось"));

        // Исключение из записи снова оставило бы задачу гашения в Faulted — ровно то
        // состояние, от которого запись и заводилась.
        await sequence.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, recorder.Closed);
    }

    [Fact]
    public void Первая_попытка_взводит_признак_гашения()
    {
        var signal = new ShutdownSignal();
        var recorder = new Recorder();
        var sequence = Create(recorder, signal, new FakeCrashLog());

        Assert.False(signal.IsShuttingDown);

        Assert.True(sequence.HandleCloseRequest());

        // Признак взводится синхронно, вместе с Hide: событие завершения псевдоконсоли,
        // доигравшееся в диспетчере сразу после этого, не должно всплыть модальным окном
        // поверх пустого места. Без этой проводки тишина докладчика о сбоях была бы
        // подключена к тому, что никто не включает.
        Assert.True(signal.IsShuttingDown);

        recorder.Complete();
    }

    [Fact]
    public void Потолок_выше_честного_медленного_гашения()
    {
        // Одна помпа в медленном, но исправном случае тратит до 3 с на ожидание цикла,
        // до 2 с на повторное ожидание и до 5 с на освобождение псевдоконсоли
        // (TerminalPump.DisposeAsync). Эти десять секунд достижимы и на одной вкладке:
        // помпы гасятся параллельно. Потолок ниже них рвал бы не зависшее, а просто
        // медленное гашение, и невежливо прибитая оболочка стала бы штатным исходом.
        var slowestHonestPump = TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(2) + TimeSpan.FromSeconds(5);

        Assert.True(
            WindowShutdownSequence.ShutdownDeadline > slowestHonestPump,
            FormattableString.Invariant(
                $"Потолок {WindowShutdownSequence.ShutdownDeadline} не выше медленного гашения {slowestHonestPump}."));
    }

    [Fact]
    public void Настоящее_окно_прячется_прямо_из_OnClosing_и_потом_закрывается()
    {
        // Единственная проверка на настоящем WPF-окне: всё остальное здесь работает с
        // подставными Hide и Close. WPF взводит признак «окно закрывается» до того, как
        // поднимет Closing, и на попытку тронуть видимость посреди закрытия отвечает
        // исключением — прятать окно из обработчика можно только если Hide под этот
        // запрет не попадает. Вся починка держится на этом, и проверить это можно
        // только окном.
        RunOnUiThread(() =>
        {
            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            var hiddenFromClosing = false;
            var closed = false;
            var cancel = true;

            window.Closing += (_, e) =>
            {
                window.Hide();
                hiddenFromClosing = true;
                e.Cancel = cancel;
            };

            window.Closed += (_, _) => closed = true;

            window.Show();

            // Первая попытка: прячем и отменяем — ровно то, что делает главное окно.
            window.Close();

            Assert.True(hiddenFromClosing, "Closing не сработал — проверка прошла бы вхолостую.");
            Assert.False(window.IsVisible);
            Assert.False(closed);

            // Вторая: отмену снимаем — спрятанное окно обязано закрыться, иначе гашение
            // закрывало бы невидимое окно впустую и процесс висел бы вечно.
            cancel = false;
            window.Close();

            Assert.True(closed);
        });
    }

    private static void RunOnUiThread(Action body)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Проверка в потоке интерфейса не завершилась за 30 секунд.");

        if (captured is not null)
        {
            throw new InvalidOperationException("Проверка в потоке интерфейса упала.", captured);
        }
    }

    /// <summary>Ждёт, пока потолок встанет таймером на управляемых часах.</summary>
    private static async Task WaitForDeadlineTimerAsync(ManualTimeProvider time)
    {
        var giveUpAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (time.ArmedTimers == 0)
        {
            Assert.True(DateTime.UtcNow < giveUpAt, "Таймер потолка так и не встал.");
            await Task.Delay(1);
        }
    }

    private static WindowShutdownSequence Create(Recorder recorder) =>
        Create(recorder, new ShutdownSignal(), new FakeCrashLog());

    private static WindowShutdownSequence Create(Recorder recorder, ShutdownSignal signal, FakeCrashLog log) =>
        new(recorder.ShutdownAsync, recorder.Hide, recorder.Close, new ManualTimeProvider(), signal, log, Deadline);

    /// <summary>
    /// Окно, сведённое к трём счётчикам, и гашение, которым управляет тест. Ожидания тут
    /// не сон, а точки синхронизации: продолжение после <c>await</c> в тестах приходит с
    /// пула потоков, и проверять счётчики сразу после хода часов было бы гонкой.
    /// </summary>
    private sealed class Recorder
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _startCount;

        public int Started => Volatile.Read(ref _startCount);

        public int Hidden { get; private set; }

        public int Closed { get; private set; }

        public bool IsShutdownFinished => _shutdown.Task.IsCompleted;

        public Task ShutdownAsync()
        {
            Interlocked.Increment(ref _startCount);
            _started.TrySetResult();
            return _shutdown.Task;
        }

        public void Hide() => Hidden++;

        public void Close()
        {
            Closed++;
            _closed.TrySetResult();
        }

        public void Complete() => _shutdown.TrySetResult();

        public void Fail(Exception exception) => _shutdown.TrySetException(exception);

        public Task WaitForStartAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public async Task WaitForCloseAsync(WindowShutdownSequence sequence)
        {
            await _closed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Закрытие зовётся из finally, но сама задача гашения к этому моменту ещё не
            // обязательно завершена — дожидаемся и её, чтобы состояние последовательности
            // было устоявшимся.
            if (sequence.Running is { } running)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}
