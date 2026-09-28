using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Просмотр файлов на странице терминалов — та же панель вкладки, что у diff, в режиме «файл»:
/// без знаков +/−, с подсветкой синтаксиса. Реализует мост (один WebView2 на окно).
/// Отдельный интерфейс от <see cref="IDiffView"/>: координатору diff файлы не нужны (ISP).
/// </summary>
/// <remarks>
/// Показ переключает панель вкладки в режим «файл» и заменяет её содержимое; следующий
/// <see cref="IDiffView.ShowPendingAsync"/> возвращает её в режим diff. Закрывает панель
/// только пользователь — событием <see cref="IDiffView.Closed"/>, общим для обоих режимов.
/// </remarks>
public interface IFileView
{
    /// <summary>
    /// Открывает панель вкладки с файлами. Крупный текст реализация режет на части
    /// не больше 1 МБ, чтобы одно сообщение моста не держало поток интерфейса.
    /// </summary>
    ValueTask ShowFilesAsync(TerminalId terminalId, FileViewSet files, CancellationToken cancellationToken);
}
