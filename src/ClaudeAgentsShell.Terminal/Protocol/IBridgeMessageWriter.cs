using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Terminal.Protocol;

/// <summary>
/// Собирает JSON для отправки на страницу. Формат — раздел 3.2 ТЗ.
/// Сообщения панели diff (<c>diff.*</c>) — не горячий путь.
/// Горячий метод здесь один — <see cref="Out"/>; он вызывается на каждую пачку вывода,
/// поэтому реализация не должна аллоцировать промежуточные строки и объекты сверх самой base64.
/// </summary>
public interface IBridgeMessageWriter
{
    /// <summary>
    /// <c>{"type":"out","id":"t1","seq":12,"b64":"…"}</c> — пачка сырых байтов PTY.
    /// Байты не декодируются в строку ни здесь, ни выше по стеку.
    /// </summary>
    /// <param name="terminalId">Вкладка-получатель.</param>
    /// <param name="sequence">
    /// Неубывающий номер пачки в пределах вкладки. Страница возвращает его в <c>ack</c>,
    /// и по нему запись сопоставляется с ожиданием: считать квитанции по порядку нельзя —
    /// одна потерянная сдвинула бы соответствие навсегда.
    /// </param>
    /// <param name="payload">Сырые байты пачки.</param>
    string Out(TerminalId terminalId, long sequence, ReadOnlySpan<byte> payload);

    /// <summary><c>{"type":"create","id":"t1","title":"…"}</c></summary>
    string Create(TerminalId terminalId, string title);

    /// <summary><c>{"type":"show","id":"t1"}</c></summary>
    string Show(TerminalId terminalId);

    /// <summary><c>{"type":"close","id":"t1"}</c></summary>
    string Close(TerminalId terminalId);

    /// <summary><c>{"type":"exited","id":"t1","code":0}</c></summary>
    string Exited(TerminalId terminalId, int exitCode);

    /// <summary>
    /// <c>{"type":"paste.result","id":"t1","kind":"text","text":"…"}</c> — ответ на
    /// <c>paste.request</c> и <c>drop</c>. <c>kind</c>: <c>text</c> — страница вставляет
    /// <c>text</c> через <c>term.paste</c> (bracketed paste сохраняется); <c>image</c> — в буфере
    /// картинка, страница шлёт в PTY <c>ESC v</c> (Alt+V — вставка изображения в Claude Code);
    /// <c>none</c> — вставлять нечего. Поле <c>text</c> есть только у <c>text</c>.
    /// </summary>
    /// <param name="terminalId">Вкладка из запроса.</param>
    /// <param name="content">Что вставлять.</param>
    string PasteResult(TerminalId terminalId, PasteContent content);

    /// <summary><c>{"type":"diff.pending","id":"t1"}</c> — открыть панель в состоянии «строится».</summary>
    string DiffPending(TerminalId terminalId);

    /// <summary>
    /// <c>{"type":"diff.index",…}</c> — оглавление: база, корень, <c>note</c>, рабочие деревья
    /// и файлы с причиной свёртки. Форма — раздел M7 <c>docs/PROGRESS.md</c>.
    /// </summary>
    /// <param name="terminalId">Вкладка.</param>
    /// <param name="index">Оглавление.</param>
    /// <param name="worktrees">Рабочие деревья для переключателя.</param>
    /// <param name="note">Пояснение агента; <c>null</c> — нет.</param>
    /// <param name="expandFiles">Файлы, которые страница раскрывает вне бюджета.</param>
    string DiffIndex(
        TerminalId terminalId,
        DiffIndex index,
        IReadOnlyList<GitWorktree> worktrees,
        string? note,
        IReadOnlyList<string> expandFiles);

    /// <summary>
    /// <c>{"type":"diff.file",…,"part":0,"last":true,"text":"…"}</c> — содержимое файла частями.
    /// Каждое сообщение не длиннее <see cref="BridgeMessageWriter.MaxDiffMessageLength"/> символов;
    /// суррогатная пара никогда не рвётся между частями. Пустой текст — одна часть.
    /// Части собираются лениво, по одной на шаг перечисления: крупный файл не держит в памяти
    /// все сообщения сразу.
    /// </summary>
    IEnumerable<string> DiffFile(TerminalId terminalId, FileDiff file);

    /// <summary><c>{"type":"diff.error","id":"t1","path":null,"message":"…"}</c> — сбой всей панели или одного файла.</summary>
    string DiffError(TerminalId terminalId, string? path, string message);

    /// <summary><c>{"type":"diff.stale","id":"t1"}</c> — плашка «есть изменения — обновить».</summary>
    string DiffStale(TerminalId terminalId);
}
