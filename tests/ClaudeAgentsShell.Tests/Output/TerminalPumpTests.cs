using System.Text;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal;
using ClaudeAgentsShell.Terminal.Output;
using ClaudeAgentsShell.Terminal.Protocol;
using ClaudeAgentsShell.Tests.Fakes;
using Xunit;

namespace ClaudeAgentsShell.Tests.Output;

public sealed class TerminalPumpTests
{
    private static readonly TerminalId Id = new("t1");
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    [Fact]
    public async Task Мелкие_чанки_уезжают_одной_пачкой_за_кадр()
    {
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();
        var time = new ManualTimeProvider();

        await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), time);
        pump.Start();

        for (int i = 1; i <= 5; i++)
        {
            pty.Emit((byte)i);
        }

        await WaitUntilAsync(() => pty.ChunksConsumed == 5 && time.ArmedTimers == 1);

        // До кадра ничего не улетело: почанковая отправка запрещена.
        Assert.Empty(bridge.Batches);

        time.Advance(Frame);
        await WaitUntilAsync(() => bridge.Batches.Count == 1);

        Assert.Single(bridge.Batches);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bridge.AllBytes);
    }

    [Fact]
    public async Task Порог_в_64_КБ_сбрасывает_пачку_не_дожидаясь_кадра()
    {
        var options = new TerminalOptions();
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();
        var time = new ManualTimeProvider();

        await using var pump = new TerminalPump(Id, pty, bridge, options, time);
        pump.Start();

        byte[] noisy = new byte[options.FlushThresholdBytes];
        Random.Shared.NextBytes(noisy);
        pty.Emit(noisy);

        await WaitUntilAsync(() => bridge.AllBytes.Length == noisy.Length);

        Assert.Equal(noisy, bridge.AllBytes);

        // Время не двигалось: сброс сделал порог, а не кадровый таймер.
        Assert.Equal(0, time.TimerCallbacks);
    }

    [Fact]
    public async Task Кадровый_таймер_не_тикает_вхолостую()
    {
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();
        var time = new ManualTimeProvider();

        await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), time);
        pump.Start();

        // Буфер пуст — сколько бы кадров ни прошло, таймер не должен срабатывать ни разу.
        for (int i = 0; i < 20; i++)
        {
            time.Advance(Frame);
        }

        Assert.Equal(0, time.TimerCallbacks);
        Assert.Empty(bridge.Batches);

        pty.Emit(42);
        await WaitUntilAsync(() => time.ArmedTimers == 1);

        time.Advance(Frame);
        await WaitUntilAsync(() => bridge.Batches.Count == 1);
        Assert.Equal(1, time.TimerCallbacks);
        Assert.Equal(0, time.ArmedTimers);

        // После сброса таймер снят: новых срабатываний на пустом буфере нет.
        for (int i = 0; i < 20; i++)
        {
            time.Advance(Frame);
        }

        Assert.Equal(1, time.TimerCallbacks);
        Assert.Single(bridge.Batches);
    }

    [Fact]
    public async Task Неподтверждённые_записи_останавливают_чтение_из_pty()
    {
        var options = new TerminalOptions { MaxPendingWrites = 2, FlushThresholdBytes = 8 };
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge(acknowledgeImmediately: false);
        var time = new ManualTimeProvider();

        await using var pump = new TerminalPump(Id, pty, bridge, options, time);
        pump.Start();

        for (int i = 0; i < 5; i++)
        {
            pty.Emit(new byte[16]);
        }

        await WaitUntilAsync(() => bridge.Batches.Count == options.MaxPendingWrites);

        // Порог достигнут — чтение стоит, сколько бы данных ни ждало в PTY.
        await Task.Delay(100);
        Assert.Equal(options.MaxPendingWrites, bridge.Batches.Count);
        Assert.Equal(options.MaxPendingWrites, pty.ChunksConsumed);

        bridge.AcknowledgeNext();

        // Подтверждение отпускает ровно одну запись — чтение возобновляется.
        await WaitUntilAsync(() => bridge.Batches.Count == options.MaxPendingWrites + 1);
        Assert.Equal(options.MaxPendingWrites + 1, pty.ChunksConsumed);

        bridge.AcknowledgeNext();
        bridge.AcknowledgeNext();
        bridge.AcknowledgeNext();
        bridge.AcknowledgeNext();

        await WaitUntilAsync(() => bridge.AllBytes.Length == 5 * 16);
    }

    [Fact]
    public async Task Разрезанная_на_чанки_кириллица_доезжает_целиком()
    {
        const string text = "Привет, мир! ┌──┐ 🚀 ёжик";
        byte[] source = Encoding.UTF8.GetBytes(text);

        var options = new TerminalOptions();
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();
        var time = new ManualTimeProvider();

        await using var pump = new TerminalPump(Id, pty, bridge, options, time);
        pump.Start();

        // Режем ровно посреди многобайтовых последовательностей.
        int chunks = 0;
        for (int i = 0; i < source.Length; i += 3)
        {
            pty.Emit(source.Skip(i).Take(3).ToArray());
            chunks++;
        }

        await WaitUntilAsync(() => pty.ChunksConsumed == chunks && time.ArmedTimers == 1);

        time.Advance(Frame);
        await WaitUntilAsync(() => bridge.AllBytes.Length == source.Length);

        Assert.Equal(source, bridge.AllBytes);
        Assert.Equal(text, Encoding.UTF8.GetString(bridge.AllBytes));
    }

    /// <summary>
    /// Байтовая прозрачность пути ввода: что бы страница ни прислала и как бы это ни было
    /// разрезано между сообщениями, в оболочку приходит ровно то же самое. Разрез идёт по
    /// всем позициям, включая середину управляющей последовательности и середину
    /// многобайтового символа. Образцом взята обёртка bracketed paste — именно на ней
    /// потеря или перестановка байтов превратила бы многострочную вставку в серию Enter.
    /// <para>
    /// Проверяется только участок «разбор сообщения → stdin псевдоконсоли». Саму обёртку
    /// здесь задаёт константа, а не <c>term.paste</c>, и сторона страницы
    /// (<c>encodeBase64</c>/<c>decodeBase64</c>) не затрагивается.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ввод_разрезанный_между_сообщениями_доезжает_байт_в_байт()
    {
        const string pasted = "\u001b[200~первая строка\rвторая строка\r🚀 третья\u001b[201~";
        byte[] source = Encoding.UTF8.GetBytes(pasted);
        var parser = new BridgeMessageParser();

        for (int cut = 0; cut <= source.Length; cut++)
        {
            var pty = new FakePtySession();
            var bridge = new FakeTerminalBridge();
            await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), new ManualTimeProvider());

            foreach (var part in new[] { source.AsMemory(0, cut), source.AsMemory(cut) })
            {
                string json = $$"""{"type":"in","id":"t1","b64":"{{Convert.ToBase64String(part.Span)}}"}""";

                Assert.True(parser.TryParse(json, out var message));
                var input = Assert.IsType<InboundBridgeMessage.Input>(message);
                await pump.SendInputAsync(input.Data, CancellationToken.None);
            }

            Assert.True(
                source.AsSpan().SequenceEqual(pty.WrittenInput.ToArray()),
                $"Разрез на позиции {cut} исказил вставку.");
            Assert.Equal(pasted, Encoding.UTF8.GetString(pty.WrittenInput.ToArray()));
        }
    }

    [Fact]
    public async Task Нулевой_размер_в_псевдоконсоль_не_попадает()
    {
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();

        await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), new ManualTimeProvider());

        pump.Resize(new TerminalSize(0, 0));
        pump.Resize(new TerminalSize(120, -1));
        pump.Resize(new TerminalSize(120, 34));

        Assert.Equal([new TerminalSize(120, 34)], pty.Resizes);
    }

    [Fact]
    public async Task Ввод_уходит_в_stdin_псевдоконсоли()
    {
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();

        await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), new ManualTimeProvider());

        byte[] data = Encoding.UTF8.GetBytes("привет\r");
        await pump.SendInputAsync(data, CancellationToken.None);

        Assert.Equal(data, pty.WrittenInput);
    }

    [Fact]
    public async Task Завершение_оболочки_доезжает_до_страницы()
    {
        var pty = new FakePtySession();
        var bridge = new FakeTerminalBridge();

        await using var pump = new TerminalPump(Id, pty, bridge, new TerminalOptions(), new ManualTimeProvider());
        pump.Start();

        pty.RaiseExited(3);
        pty.EndOfStream();

        await WaitUntilAsync(() => bridge.ExitCodes.Count == 1);

        Assert.Equal([3], bridge.ExitCodes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Условие не выполнилось за отведённое время.");
            }

            await Task.Delay(5);
        }
    }
}
