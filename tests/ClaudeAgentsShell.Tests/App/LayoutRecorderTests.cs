using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Дебаунс, снимок в момент записи и заморозка регистратора раскладки.</summary>
public sealed class LayoutRecorderTests
{
    private static readonly TimeSpan Delay = LayoutRecorder.DebounceDelay;

    private static WorkspaceLayout Layout(string sessionId) =>
        new(null, [new ProjectLayout(Guid.Empty, 0, [new TabLayout(sessionId, null)])]);

    private sealed class Harness
    {
        public Harness(QueuedUiDispatcher? dispatcher = null)
        {
            Recorder = Store.CreateRecorder(Time, dispatcher);
        }

        public ManualTimeProvider Time { get; } = new();

        public FakeLayoutStore Store { get; } = new();

        public LayoutRecorder Recorder { get; }

        public string Current { get; set; } = "s1";

        public WorkspaceLayout Capture() => Layout(Current);
    }

    [Fact]
    public void Серия_изменений_даёт_одну_запись_после_окна_тишины()
    {
        var harness = new Harness();
        harness.Recorder.Start(harness.Capture);
        harness.Time.Advance(Delay);
        harness.Store.Saves.Clear();

        for (var i = 0; i < 5; i++)
        {
            harness.Recorder.Signal();
            harness.Time.Advance(Delay / 2);
        }

        Assert.Empty(harness.Store.Saves);

        harness.Time.Advance(Delay);

        Assert.Single(harness.Store.Saves);
    }

    [Fact]
    public void Снимок_берётся_в_момент_записи_а_не_сигнала()
    {
        var harness = new Harness();
        harness.Recorder.Start(harness.Capture);

        harness.Recorder.Signal();
        harness.Current = "s2";
        harness.Time.Advance(Delay);

        Assert.Equal("s2", Assert.Single(harness.Store.Saves).Projects[0].Tabs[0].SessionId);
    }

    [Fact]
    public void До_старта_сигналы_ничего_не_пишут()
    {
        var harness = new Harness();

        harness.Recorder.Signal();
        harness.Time.Advance(Delay * 4);

        Assert.False(harness.Recorder.IsRecording);
        Assert.Empty(harness.Store.Saves);
        Assert.Equal(0, harness.Time.ArmedTimers);
    }

    [Fact]
    public async Task Заморозка_до_старта_не_пишет_и_старт_после_неё_не_включает_запись()
    {
        // Окно закрыли, не дождавшись восстановления (или страница не поднялась вовсе):
        // пустая раскладка не должна лечь поверх сохранённой.
        var harness = new Harness();

        await harness.Recorder.FlushAndFreezeAsync(CancellationToken.None);
        harness.Recorder.Start(harness.Capture);
        harness.Recorder.Signal();
        harness.Time.Advance(Delay * 4);

        Assert.False(harness.Recorder.IsRecording);
        Assert.Empty(harness.Store.Saves);
    }

    [Fact]
    public async Task Сброс_при_закрытии_пишет_отложенное_сразу_и_дальше_ничего()
    {
        var harness = new Harness();
        harness.Recorder.Start(harness.Capture);

        harness.Recorder.Signal();
        await harness.Recorder.FlushAndFreezeAsync(CancellationToken.None);

        Assert.Equal("s1", Assert.Single(harness.Store.Saves).Projects[0].Tabs[0].SessionId);

        // Гашение: вкладки умирают, сигналы продолжают приходить.
        harness.Current = "мёртвая";
        harness.Recorder.Signal();
        harness.Time.Advance(Delay * 4);

        Assert.Single(harness.Store.Saves);
        Assert.Equal(0, harness.Time.ArmedTimers);
    }

    [Fact]
    public async Task Сброс_без_изменений_диск_не_трогает()
    {
        var harness = new Harness();
        harness.Recorder.Start(harness.Capture);
        harness.Time.Advance(Delay);

        await harness.Recorder.FlushAndFreezeAsync(CancellationToken.None);

        Assert.Single(harness.Store.Saves);
    }

    [Fact]
    public void Срабатывание_таймера_после_заморозки_не_пишет()
    {
        // Таймер уже сработал и поставил запись в очередь интерфейса, а до её выполнения
        // пользователь закрыл окно: проверка обязана повториться при выполнении.
        var dispatcher = new QueuedUiDispatcher();
        var harness = new Harness(dispatcher);
        harness.Recorder.Start(harness.Capture);
        harness.Time.Advance(Delay);
        Assert.True(dispatcher.HasPending);

        harness.Recorder.Freeze();
        dispatcher.Drain();

        Assert.Empty(harness.Store.Saves);
    }

    [Fact]
    public async Task Сорванная_запись_повторяется_при_закрытии()
    {
        var harness = new Harness();
        harness.Recorder.Start(harness.Capture);
        harness.Store.SaveFailure = new IOException("диск занят");

        harness.Time.Advance(Delay);
        Assert.Empty(harness.Store.Saves);

        await harness.Recorder.FlushAndFreezeAsync(CancellationToken.None);

        Assert.Single(harness.Store.Saves);
    }
}
