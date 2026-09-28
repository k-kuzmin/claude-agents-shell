using ClaudeAgentsShell.App.ViewModels;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.App.Diff;

/// <summary>
/// Панель вкладки одна на diff и просмотр файлов (<c>show_file</c>). Её хозяин — координатор
/// diff: у него замок отправки на страницу и значок фоновой вкладки. Координатор файлов
/// показывает файлы через него, чтобы сообщения двух режимов не перемешались, а значок
/// <see cref="TabViewModel.HasPendingDiff"/> ставился и снимался в одном месте.
/// </summary>
public interface IDiffPanelHost
{
    /// <summary>
    /// Отметка последнего запроса diff. Взятая до чтения файлов, она позволяет понять, что
    /// пользователь открыл diff позже, чем агент попросил файлы, — тогда файлы не показываются.
    /// </summary>
    long DiffRequestStamp { get; }

    /// <summary>
    /// Отдаёт панель вкладки файлам: под замком отправки списывает diff вкладки (его поздние
    /// ответы на страницу уже не уйдут) и выполняет <paramref name="send"/>.
    /// </summary>
    /// <param name="terminalId">Вкладка.</param>
    /// <param name="stamp"><see cref="DiffRequestStamp"/> на момент запроса файлов.</param>
    /// <param name="send">Отправка файлов на страницу.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns><c>false</c> — после запроса файлов начался diff этой вкладки, либо хозяин освобождается; ничего не отправлено.</returns>
    Task<bool> ShowFilesAsync(
        TerminalId terminalId,
        long stamp,
        Func<CancellationToken, ValueTask> send,
        CancellationToken cancellationToken);

    /// <summary>
    /// Значок на кнопке diff фоновой вкладки; снимается, когда вкладка станет активной.
    /// Вызывается в потоке интерфейса.
    /// </summary>
    void SetPendingBadge(TabViewModel tab);
}
