using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Domain;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Координатор состояний вкладок (раздел 5.3 ТЗ): хуки превращаются в состояние вкладки
/// и короткое имя сессии, а всё непонятное молча пропускается. Другого источника состояния,
/// кроме хуков, нет — ни вывода агента, ни клавиатуры (раздел 7 CLAUDE.md).
/// </summary>
public sealed class SessionStateCoordinatorTests
{
    private const string ProjectPath = @"D:\src\alpha";
    private const string HookPath = @"D:\src\alpha-from-hook";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// Ожидаемый бюджет чтений транскрипта за сессию. Записан числом намеренно — это
    /// утверждение о поведении, а не копия закрытой константы координатора: изменят бюджет —
    /// тесты обязаны упасть, а не подстроиться.
    /// </summary>
    /// <remarks>
    /// Три: одна попытка по <c>SessionStart</c> у свежей сессии почти всегда пустая (сообщения
    /// пользователя в транскрипте ещё нет), остаются две содержательные. Меньше нельзя —
    /// по замеру M5-0 на 785 транскриптах заголовок появляется позже первого <c>Stop</c>
    /// в 2 случаях (81 до правки разбора слэш-команд), и причина остатка — задержка сброса
    /// файла на диск, от которой спасает только повтор.
    /// </remarks>
    private const int MaxTitleAttempts = 3;

    [Fact]
    public async Task SessionStart_переводит_вкладку_в_простаивает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Stop_переводит_вкладку_в_ждёт_ввода()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task SessionEnd_снимает_маркер()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.RaiseHook(HookKind.SessionEnd, tab);

        Assert.Equal(TabState.Unknown, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task UserPromptSubmit_переводит_вкладку_в_работает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.UserPromptSubmit, tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task UserPromptSubmit_в_свежей_сессии_тоже_переводит_в_работает()
    {
        // У сессии, которая в «ждёт ввода» ещё не была, точка иначе не менялась бы вовсе.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.RaiseHook(HookKind.UserPromptSubmit, tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Полный_цикл_состояний_проходит_по_порядку()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SessionEnd, tab);

        Assert.Equal(
            new[] { TabState.Idle, TabState.Busy, TabState.AwaitingInput, TabState.Busy, TabState.Unknown },
            harness.Sink.StateLog.Select(entry => entry.State));
    }

    [Fact]
    public async Task Stop_при_работающем_сабагенте_даёт_фоновую_работу()
    {
        // Ход агента кончился, но сессия продолжится сама: от человека сейчас ничего не нужно.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Последний_SubagentStop_возвращает_вкладку_в_работает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);

        // Главный агент вот-вот продолжит сам — «ждёт ввода» здесь было бы враньём.
        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Пока_сабагенты_не_кончились_вкладка_остаётся_в_фоновой_работе()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.SubagentStop, tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task SubagentStart_сам_по_себе_состояние_не_меняет()
    {
        // Сабагента запускает работающий агент — маркер уже стоит верный.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        var before = harness.Sink.StateLog.Count;

        harness.RaiseHook(HookKind.SubagentStart, tab);

        Assert.Equal(before, harness.Sink.StateLog.Count);
    }

    [Fact]
    public async Task Сабагент_закончил_до_конца_хода_и_Stop_даёт_ждёт_ввода()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Лишний_SubagentStop_не_сбивает_ждёт_ввода()
    {
        // Опоздавший или задвоенный хук не должен воровать маркер, которого ждёт пользователь.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Лишний_SubagentStop_не_уводит_счётчик_в_минус()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);

        // Отрицательный остаток скрыл бы настоящий запуск сабагента: Stop дал бы «ждёт ввода».
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Потерянный_SubagentStop_живёт_до_конца_сессии()
    {
        // Хук идёт через curl и может не доехать. Снять дрейф на отправке промпта нельзя:
        // промпт прилетает и посреди чужой фоновой работы, и обнуление стёрло бы живых
        // сабагентов. Поэтому дрейф держится до границы сессии — вкладка показывает «занята
        // своей работой». Это мягкий отказ: зайти и написать в неё человеку никто не мешает,
        // а обратная ошибка — «ждёт ввода» на работающей вкладке — дороже.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        // А вот начало сессии его снимает: там достоверно известно, что сабагентов нет.
        harness.RaiseHook(HookKind.SessionStart, tab, source: "clear");
        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Сжатие_контекста_не_гасит_работающую_вкладку()
    {
        // Автосжатие срабатывает посреди хода и приходит тем же SessionStart, что и запуск.
        // Выставить на нём «простаивает» значило бы зажечь серую точку, пока агент работает.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SessionStart, tab, source: "compact");

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);
        Assert.DoesNotContain(TabState.Idle, harness.Sink.StateLog.Select(entry => entry.State));
    }

    [Fact]
    public async Task Сжатие_контекста_не_снимает_счётчик_сабагентов()
    {
        // Первая из трёх последовательностей, роняющих счётчик: сжатие посреди хода
        // с работающим сабагентом. Обнуление дало бы на следующем Stop ложное «ждёт ввода».
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.SessionStart, tab, source: "compact");
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);
    }

    [Theory]
    [InlineData("schedule_wakeup")]
    [InlineData("loop_wakeup")]
    [InlineData("system")]
    [InlineData("sdk")]
    public async Task Машинный_промпт_не_снимает_счётчик_сабагентов(string source)
    {
        // Вторая последовательность: пробуждение по расписанию или по /loop приходит тем же
        // UserPromptSubmit, но человека за ним нет и ход сабагентов не прерывает.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab, source: "user");
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.UserPromptSubmit, tab, source: source);

        // В «работает» переводят все источники: ход начинается в любом случае.
        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Промпт_досланный_в_фоновую_работу_не_снимает_счётчик()
    {
        // Третья последовательность, и она про живого человека: «занята своей работой» ровно
        // на то и намекает, что туда можно зайти и написать. Счётчик при этом остаётся живым.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab, source: "user");
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.UserPromptSubmit, tab, source: "user");
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        // Сабагент закончил — вкладка возвращается к человеку.
        harness.RaiseHook(HookKind.SubagentStop, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task StopFailure_заканчивает_ход_оборванный_ошибкой()
    {
        // Ход упал на ошибке API или был прерван — Stop тогда может не прийти вовсе,
        // и без этого хука вкладка осталась бы в «работает» навсегда.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);

        Assert.Equal(TabState.Busy, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.StopFailure, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task StopFailure_при_работающем_сабагенте_даёт_фоновую_работу()
    {
        // Правила те же, что у Stop: сабагент оборванного хода мог пережить сам ход.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.StopFailure, tab);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    [InlineData("fork")]
    public async Task SessionStart_настоящей_границей_хода_снимает_счётчик_фоновой_работы(string? source)
    {
        // Сессия поднялась заново (startup/resume/fork) или потеряла контекст (clear): живых
        // сабагентов у неё нет. Отсутствующий source ведёт себя так же — формат нестабилен
        // (раздел 7 CLAUDE.md), и по незнанию поведение остаётся прежним.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.SessionStart, tab, source: source);

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);

        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task SessionEnd_снимает_счётчик_фоновой_работы()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.SessionEnd, tab);

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Счётчики_вкладок_не_перемешиваются()
    {
        using var harness = new Harness();
        var first = await harness.StartWithTabAsync();
        var second = await harness.OpenTabAsync();

        harness.RaiseHook(HookKind.SubagentStart, first);
        harness.RaiseHook(HookKind.Stop, second);

        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[second]);

        harness.RaiseHook(HookKind.Stop, first);

        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[first]);
    }

    [Fact]
    public async Task Неизвестный_токен_ничего_не_меняет()
    {
        using var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Hooks.Raise(HookKind.SessionStart, "tok-чужой", SessionId, ProjectPath);
        harness.Hooks.Raise(HookKind.Stop, null, SessionId, ProjectPath);
        harness.Pump();

        Assert.Empty(harness.Sink.StateLog);
        Assert.Empty(harness.History.Requested);
    }

    [Fact]
    public async Task Незарегистрированный_хук_состояние_не_трогает()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Unknown, tab);

        Assert.Empty(harness.Sink.StateLog);
    }

    [Fact]
    public async Task Хук_доходит_до_вкладки_только_через_диспетчер()
    {
        // HttpListener поднимает событие в потоке пула: трогать полосу вкладок оттуда нельзя.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        // Намеренно без Pump: до прокрутки очереди событие не должно доходить до вкладки.
        harness.Hooks.Raise(HookKind.SessionStart, harness.Workspace.TokenFor(tab), SessionId, ProjectPath);

        Assert.Empty(harness.Sink.StateLog);
        Assert.Equal(1, harness.Dispatcher.PostCount);

        harness.Pump();

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task SessionStart_ставит_короткое_имя_из_первого_сообщения()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку и добавь тесты");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку и добавь тесты", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Транскрипт_ищется_в_каталоге_из_хука()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "привет");

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: HookPath);
        await harness.SettleAsync();

        Assert.Equal((HookPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Без_каталога_в_хуке_берётся_каталог_вкладки()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "привет");

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: null);
        await harness.SettleAsync();

        Assert.Equal((ProjectPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Без_каталога_и_без_вкладки_транскрипт_не_читается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync(knownToSink: false);

        harness.RaiseHook(HookKind.SessionStart, tab, workingDirectory: null);
        await harness.SettleAsync();

        Assert.Empty(harness.History.Requested);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Без_идентификатора_сессии_транскрипт_не_читается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: null);
        await harness.SettleAsync();

        Assert.Empty(harness.History.Requested);
        Assert.Empty(harness.Sink.ShortTitles);
    }

    [Fact]
    public async Task Отсутствие_транскрипта_не_роняет_и_имя_не_портит()
    {
        // Файл транскрипта создаётся не в момент SessionStart — это не ошибка,
        // заголовок вкладки остаётся «новая сессия».
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ShortTitles);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Имя_дочитывается_по_Stop_если_транскрипта_ещё_не_было()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();
        Assert.Empty(harness.Sink.ShortTitles);

        // К моменту Stop агент уже ответил, значит первое сообщение в транскрипте есть.
        harness.History.Seed(SessionId, "почини сборку");
        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
        Assert.Equal(2, harness.History.Requested.Count);
    }

    [Fact]
    public async Task Stop_без_SessionStart_тоже_даёт_имя()
    {
        // Приёмник мог подняться уже после старта сессии: тогда первым и единственным
        // известным хуком вкладки окажется Stop, и заголовок берётся из него.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");

        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
        Assert.Equal((ProjectPath, SessionId), harness.History.Requested.Single());
    }

    [Fact]
    public async Task Первый_SessionStart_имя_не_сбрасывает()
    {
        // О прежней сессии вкладки ничего не известно — сбрасывать нечего.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ResetTitles);
    }

    [Fact]
    public async Task Смена_сессии_во_вкладке_сбрасывает_имя_сразу()
    {
        // Раздел 6.3 ТЗ: имя принадлежит сессии, и имя закончившейся висеть не должно —
        // в том числе пока транскрипт новой ещё не разобрался.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();
        Assert.Equal("первая сессия", harness.Sink.ShortTitles[tab]);

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: other);
        await harness.SettleAsync();

        Assert.Equal([tab], harness.Sink.ResetTitles);
        Assert.Empty(harness.Sink.ShortTitles);
    }

    [Fact]
    public async Task Тот_же_идентификатор_сессии_имя_не_трогает()
    {
        // SessionStart приходит и на сжатие контекста: безусловный сброс заставил бы
        // заголовок мигать «новая сессия» и обратно на каждом сжатии.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ResetTitles);
        Assert.Equal("первая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Пустой_идентификатор_сессии_имя_не_сбрасывает()
    {
        // Пустой session_id означает «неизвестно», а по незнанию имя не трогаем.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: null);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ResetTitles);
        Assert.Equal("первая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Опоздавшее_чтение_прежней_сессии_имя_не_перебивает()
    {
        // Чтение транскрипта первой сессии доезжает уже после того, как во вкладке
        // началась вторая: заголовок обязан остаться сброшенным.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        var gate = new TaskCompletionSource();
        harness.History.Gates[SessionId] = gate;
        harness.RaiseHook(HookKind.SessionStart, tab);
        Assert.False(harness.Coordinator.PendingTitleWork.IsCompleted);

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: other);

        // Транскрипт первой сессии доехал только теперь — и он уже чужой.
        gate.SetResult();
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ShortTitles);
        Assert.Equal([tab], harness.Sink.ResetTitles);
    }

    [Fact]
    public async Task Заголовок_появившийся_не_с_первого_хода_всё_равно_доезжает()
    {
        // Первым ходом бывает слэш-команда (/init, /review, промпт MCP): её строка обёрнута
        // в <command-name> и заголовком не становится. Настоящий запрос приходит следующим
        // сообщением, и вкладка обязана получить имя, а не остаться «новой сессией» навсегда.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, title: null);

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();
        Assert.Empty(harness.Sink.ShortTitles);

        // Пользователь наконец написал настоящий запрос.
        harness.History.Seed(SessionId, "почини сборку");
        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Число_чтений_за_сессию_ограничено()
    {
        // Повтор висит на Stop, то есть на каждом ответе агента: без потолка транскрипт
        // перечитывался бы весь сеанс.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, title: null);

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        for (var i = 0; i < 10; i++)
        {
            harness.RaiseHook(HookKind.Stop, tab);
            await harness.SettleAsync();
        }

        Assert.Equal(MaxTitleAttempts, harness.History.Requested.Count);
        Assert.Empty(harness.Sink.ShortTitles);
    }

    [Fact]
    public async Task Сжатие_контекста_бюджет_заголовка_не_тратит()
    {
        // Бюджет мал (см. MaxTitleAttempts), а автосжатие за длинную сессию срабатывает
        // не раз: тратить на него чтения нельзя — транскрипт к этому моменту не подрос.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, title: null);

        for (var i = 0; i < 5; i++)
        {
            harness.RaiseHook(HookKind.SessionStart, tab, source: "compact");
            await harness.SettleAsync();
        }

        Assert.Empty(harness.History.Requested);
    }

    [Fact]
    public async Task Оборванный_ход_тоже_даёт_попытку_заголовка()
    {
        // Если первый же ход упал на ошибке API, Stop не придёт, и без этой попытки вкладка
        // осталась бы с «новая сессия» до следующего хода. Лишних чтений это не даёт:
        // бюджет общий.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, title: null);

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.History.Seed(SessionId, "почини сборку");
        harness.RaiseHook(HookKind.StopFailure, tab);
        await harness.SettleAsync();

        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Исчерпанный_бюджет_у_новой_сессии_начинается_заново()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, title: null);

        for (var i = 0; i < MaxTitleAttempts + 2; i++)
        {
            harness.RaiseHook(HookKind.Stop, tab);
            await harness.SettleAsync();
        }

        Assert.Equal(MaxTitleAttempts, harness.History.Requested.Count);

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(other, "вторая сессия");
        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: other);
        await harness.SettleAsync();

        Assert.Equal("вторая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Stop_с_новым_идентификатором_чтение_ставит_даже_после_Resolved()
    {
        // SessionStart мог не дойти: curl не достучался или пришёл без session_id. Тогда первым
        // хуком новой сессии окажется Stop, и стадия поиска от прежней сессии не должна его
        // блокировать — иначе на экране навсегда останется имя закончившейся.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();
        Assert.Equal("первая сессия", harness.Sink.ShortTitles[tab]);

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(other, "вторая сессия");
        harness.RaiseHook(HookKind.Stop, tab, sessionId: other);
        await harness.SettleAsync();

        Assert.Equal("вторая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Одновременные_запросы_второго_чтения_не_порождают()
    {
        // Чтение уже идёт — Stop поверх него нового прохода по файлу не ставит.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");

        var gate = new TaskCompletionSource();
        harness.History.Gates[SessionId] = gate;

        harness.RaiseHook(HookKind.SessionStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.Stop, tab);

        gate.SetResult();
        await harness.SettleAsync();

        Assert.Single(harness.History.Requested);
        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Сбой_чтения_повтор_не_закрывает()
    {
        // Сбой — не «искали и не нашли»: спросить позже имеет смысл.
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");
        harness.History.ReadFailure = new IOException("файл занят");

        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();
        Assert.Empty(harness.Sink.ShortTitles);

        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Equal(2, harness.History.Requested.Count);
        Assert.Equal("почини сборку", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Готовое_имя_второй_раз_не_вычитывается()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        await harness.SettleAsync();

        Assert.Single(harness.History.Requested);
    }

    [Fact]
    public async Task Новая_сессия_во_вкладке_перечитывает_имя()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "первая сессия");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        const string other = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(other, "вторая сессия");
        harness.RaiseHook(HookKind.SessionStart, tab, sessionId: other);
        await harness.SettleAsync();

        Assert.Equal("вторая сессия", harness.Sink.ShortTitles[tab]);
    }

    [Fact]
    public async Task Сбой_чтения_транскрипта_не_роняет_приложение()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();
        harness.History.Seed(SessionId, "почини сборку");
        harness.History.ReadFailure = new IOException("файл занят");

        harness.RaiseHook(HookKind.SessionStart, tab);
        await harness.SettleAsync();

        Assert.Empty(harness.Sink.ShortTitles);
        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Заголовки_нескольких_вкладок_доезжают_независимо()
    {
        using var harness = new Harness();
        var first = await harness.StartWithTabAsync();
        var second = await harness.OpenTabAsync();

        const string secondSession = "99999999-8888-7777-6666-555555555555";
        harness.History.Seed(SessionId, "первая вкладка");
        harness.History.Seed(secondSession, "вторая вкладка");

        harness.RaiseHook(HookKind.SessionStart, first);
        harness.RaiseHook(HookKind.SessionStart, second, sessionId: secondSession);
        await harness.SettleAsync();

        Assert.Equal("первая вкладка", harness.Sink.ShortTitles[first]);
        Assert.Equal("вторая вкладка", harness.Sink.ShortTitles[second]);
    }

    [Fact]
    public async Task StartAsync_поднимает_приёмник_хуков()
    {
        using var harness = new Harness();
        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        Assert.True(harness.Hooks.Started);
    }

    [Fact]
    public async Task Сбой_подъёма_приёмника_не_роняет_запуск_и_не_снимает_подписку()
    {
        // Раздел 5.3 ТЗ: без хуков вкладки живут без маркеров, и это не повод показывать ошибку.
        // Проверяется ровно то, что отличает сбойный запуск от удачного: приёмник позвали,
        // он не поднялся, исключение наружу не вышло — а подписка осталась и работает.
        // Снимет её только Dispose.
        using var harness = new Harness();
        harness.Hooks.StartFailure = new InvalidOperationException("порт занят");

        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        Assert.True(harness.Hooks.StartAttempted);
        Assert.False(harness.Hooks.Started);

        var tab = await harness.OpenTabAsync();

        // Приёмник, который не поднялся, событий не родит — и вкладка живёт без маркера.
        Assert.False(harness.Sink.States.ContainsKey(tab));

        // Но подписка на месте: если событие всё-таки придёт, координатор его обработает.
        // Без этого утверждения тест не отличал бы оставленную подписку от снятой.
        harness.RaiseHook(HookKind.SessionStart, tab);

        Assert.Equal(TabState.Idle, harness.Sink.States[tab]);
    }

    [Fact]
    public void Приёмник_без_запуска_не_трогается()
    {
        // Обратная сторона предыдущего теста: «не поднялся» и «не звали» — разные вещи.
        using var harness = new Harness();

        Assert.False(harness.Hooks.StartAttempted);
        Assert.False(harness.Hooks.Started);
    }

    [Fact]
    public async Task Приёмник_хуков_координатором_не_освобождается()
    {
        // Владеет им контейнер: двойное освобождение — источник тихих гонок при выходе.
        var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();

        Assert.False(harness.Hooks.Disposed);
    }

    [Fact]
    public async Task После_Dispose_события_вкладок_не_трогают()
    {
        var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();
        harness.RaiseHook(HookKind.SessionStart, tab);

        Assert.Empty(harness.Sink.StateLog);
    }

    [Fact]
    public async Task Повторный_Dispose_безопасен()
    {
        var harness = new Harness();
        await harness.StartWithTabAsync();

        harness.Coordinator.Dispose();
        harness.Coordinator.Dispose();
    }

    [Fact]
    public async Task Dispose_после_сбоя_запуска_безопасен()
    {
        var harness = new Harness();
        harness.Hooks.StartFailure = new InvalidOperationException("порт занят");
        await harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None);

        harness.Coordinator.Dispose();
    }

    [Fact]
    public async Task Запуск_после_Dispose_отклоняется()
    {
        var harness = new Harness();
        harness.Coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => harness.Coordinator.StartAsync(harness.Sink, CancellationToken.None));
    }

    /// <remarks>
    /// Диспетчер всегда с очередью, а не встроенный: чтение транскрипта уходит в поток пула,
    /// и встроенный исполнял бы колбэк «только для потока интерфейса» прямо там. Тогда словарь
    /// сессий внутри координатора трогали бы два потока сразу — гонка, которая проявляется
    /// не всегда. С очередью колбэки исполняет только поток теста, в <see cref="Harness.Pump"/>.
    /// </remarks>
    [Fact]
    public async Task Смерть_процесса_снимает_маркер_вкладки()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.Stop, tab);
        Assert.Equal(TabState.AwaitingInput, harness.Sink.States[tab]);

        // `SessionEnd` от убитой оболочки не придёт: маркер обязан сняться по выходу процесса.
        harness.RaiseExited(tab);

        Assert.Equal(TabState.Unknown, harness.Sink.States[tab]);
    }

    [Fact]
    public async Task Опоздавший_SubagentStop_не_оживляет_мёртвую_вкладку()
    {
        using var harness = new Harness();
        var tab = await harness.StartWithTabAsync();

        harness.RaiseHook(HookKind.UserPromptSubmit, tab);
        harness.RaiseHook(HookKind.SubagentStart, tab);
        harness.RaiseHook(HookKind.Stop, tab);
        Assert.Equal(TabState.BackgroundWork, harness.Sink.States[tab]);

        // Процесс убит в «фоновой работе», а `curl` сабагента был запущен раньше и доезжает
        // уже после смерти вкладки.
        harness.RaiseExited(tab);
        harness.RaiseHook(HookKind.SubagentStop, tab);

        Assert.Equal(TabState.Unknown, harness.Sink.States[tab]);
    }

    private sealed class Harness : IDisposable
    {
        private int _counter;

        public Harness()
        {
            Dispatcher = new QueuedUiDispatcher();
            Coordinator = new SessionStateCoordinator(Hooks, Workspace, History, Dispatcher);
        }

        public QueuedUiDispatcher Dispatcher { get; }

        public FakeHookListener Hooks { get; } = new();

        public FakeTerminalWorkspace Workspace { get; } = new();

        public FakeSessionHistoryReader History { get; } = new();

        public FakeTabStateSink Sink { get; } = new();

        public SessionStateCoordinator Coordinator { get; }

        /// <summary>Поднимает координатор и открывает вкладку, о которой знает полоса вкладок.</summary>
        public async Task<TerminalId> StartWithTabAsync(bool knownToSink = true)
        {
            await Coordinator.StartAsync(Sink, CancellationToken.None);
            return await OpenTabAsync(knownToSink);
        }

        /// <summary>Открывает ещё одну вкладку в наборе.</summary>
        public async Task<TerminalId> OpenTabAsync(bool knownToSink = true)
        {
            var project = new ProjectDefinition(
                Guid.NewGuid(), "alpha", ProjectPath, ShellKind.Pwsh, PreLaunch: null, ExtraArgs: [], Order: _counter++);

            var tab = await Workspace.OpenAsync(project, new SessionLaunch.NewSession(), CancellationToken.None);
            if (knownToSink)
            {
                Sink.SetWorkingDirectory(tab, ProjectPath);
            }

            return tab;
        }

        /// <summary>Отправляет хук от имени конкретной вкладки и сразу прокручивает диспетчер.</summary>
        public void RaiseHook(
            HookKind kind,
            TerminalId tab,
            string? sessionId = SessionId,
            string? workingDirectory = ProjectPath,
            string? source = null)
        {
            Hooks.Raise(kind, Workspace.TokenFor(tab), sessionId, workingDirectory, source);
            Pump();
        }

        /// <summary>Сообщает о смерти процесса вкладки и сразу прокручивает диспетчер.</summary>
        public void RaiseExited(TerminalId tab, int exitCode = 1)
        {
            Workspace.RaiseExited(tab, exitCode);
            Pump();
        }

        /// <summary>Прокручивает очередь диспетчера — аналог кадра потока интерфейса.</summary>
        public void Pump() => Dispatcher.Drain();

        /// <summary>
        /// Ждёт, пока догонит фоновое чтение транскрипта, и прокручивает очередь диспетчера.
        /// </summary>
        public async Task SettleAsync()
        {
            // Пока очередь не прокручена, чтение могло и не начаться: его ставит ApplyHook,
            // а он сам приходит через диспетчер.
            Pump();
            await Coordinator.PendingTitleWork;
            Pump();
        }

        public void Dispose() => Coordinator.Dispose();
    }
}
