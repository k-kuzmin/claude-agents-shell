using System.Text;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Terminal.Pty;
using ClaudeAgentsShell.Terminal.Shells;
using Xunit;

namespace ClaudeAgentsShell.Tests.Pty;

/// <summary>
/// Живая проверка цепочки ConPTY: пайпы, псевдоконсоль, список атрибутов потока, запуск
/// процесса, чтение вывода, ресайз и освобождение без зависания. GUI и WebView2 не нужны.
/// <para>
/// Результат не зависит от того, из-под чего запущены тесты, только пока в
/// <c>STARTUPINFO.dwFlags</c> выставлен <c>STARTF_USESTDHANDLES</c>. Без него
/// <c>CreateProcess</c> отдаёт дочернему процессу стандартные хэндлы родителя, оболочка
/// пишет в чужую консоль, и в пайп приходит только преамбула ConPTY. Если этот тест
/// когда-нибудь снова начнёт зависеть от окружения запуска — смотреть надо туда.
/// </para>
/// </summary>
public sealed class ConPtySessionTests
{
    private const string Marker = "MARKER-OUTPUT-OK";

    [Fact]
    public async Task Оболочка_пишет_свой_вывод_в_псевдоконсоль()
    {
        await using var session = StartShell($"Write-Host '{Marker}'");

        byte[] output = await ReadUntilAsync(session, Marker, TimeSpan.FromSeconds(30));

        Assert.True(
            Contains(output, Marker),
            "В выводе псевдоконсоли нет маркера. Получено: " + Describe(output));
    }

    [Fact]
    public async Task Кириллица_и_эмодзи_доезжают_через_псевдоконсоль_без_потерь()
    {
        // Кириллица, символы рамок, эмодзи из суррогатной пары и составное эмодзи:
        // всё это многобайтовые последовательности, которые нельзя рвать при склейке.
        const string text = "Привет-мир-ёж-┌─┐-🚀-✅";

        // Вывод читается сырыми байтами и склеивается — декодируем только в самом конце.
        await using var session = StartShell(
            $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Write-Host '{text}'");

        byte[] output = await ReadUntilAsync(session, text, TimeSpan.FromSeconds(30));

        Assert.Contains(text, Encoding.UTF8.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Псевдоконсоль_переживает_ресайз_и_закрывается_без_зависания()
    {
        var session = StartShell("Start-Sleep -Seconds 30");

        session.Resize(new TerminalSize(120, 40));
        session.Resize(new TerminalSize(0, 0));
        session.Resize(new TerminalSize(80, 25));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await session.DisposeAsync();
        stopwatch.Stop();

        // Неверный порядок закрытия (пайпы раньше ClosePseudoConsole) вешает выход намертво.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Закрытие псевдоконсоли заняло {stopwatch.Elapsed}.");
    }

    private static IPtySession StartShell(string command)
    {
        var shell = new WindowsPowerShellProvider().TryResolve();
        Assert.NotNull(shell);

        var startInfo = new PtyStartInfo(
            shell with { Arguments = ["-NoLogo", "-NoProfile", "-Command", command] },
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            new TerminalSize(80, 25),
            new Dictionary<string, string> { ["TERM"] = "xterm-256color" });

        return new ConPtySessionFactory().Create(startInfo);
    }

    private static async Task<byte[]> ReadUntilAsync(IPtySession session, string marker, TimeSpan timeout)
    {
        var collected = new List<byte>();
        byte[] buffer = new byte[4096];
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            while (true)
            {
                int read = await session.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }

                collected.AddRange(buffer.AsSpan(0, read).ToArray());

                if (Contains(collected, marker))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Вернём то, что успели прочитать: сообщение об ошибке покажет содержимое.
        }

        return [.. collected];
    }

    /// <summary>
    /// Поиск по сырым байтам: игла кодируется в UTF-8, иначе кириллица и эмодзи
    /// не совпали бы никогда и проверка была бы фиктивной.
    /// </summary>
    private static bool Contains(IReadOnlyList<byte> haystack, string needle)
    {
        byte[] pattern = Encoding.UTF8.GetBytes(needle);
        if (haystack.Count < pattern.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Count - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (haystack[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(byte[] output) =>
        output.Length == 0
            ? "пусто"
            : $"{output.Length} байт: {Encoding.UTF8.GetString(output).Replace("\u001b", "<ESC>", StringComparison.Ordinal)}";
}
