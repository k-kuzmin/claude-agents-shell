using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Application.Ports;

/// <summary>
/// Читает diff из git по запросу — по клику пользователя или по вызову <c>show_diff</c>.
/// Периодического опроса нет. Репозиторий пользователя только читается: <c>git</c> запускается
/// с <c>GIT_OPTIONAL_LOCKS=0</c> и никогда не трогает индекс или рабочее дерево.
/// </summary>
public interface IGitDiffReader
{
    /// <summary>
    /// Строит оглавление: изменённые файлы ветки против базы, закоммиченное и незакоммиченное
    /// вместе, плюс неотслеживаемые. Содержимого файлов нет — оно грузится по одному.
    /// </summary>
    /// <exception cref="DiffUnavailableException">Diff построить нельзя.</exception>
    Task<DiffIndex> ListChangesAsync(DiffRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Diff одного файла из оглавления. Вывод git читается потоком с потолком; при превышении
    /// процесс снимается, а результат помечен <see cref="FileDiff.Truncated"/>.
    /// </summary>
    /// <exception cref="DiffUnavailableException">Diff файла построить нельзя.</exception>
    Task<FileDiff> ReadFileDiffAsync(DiffIndex index, DiffFileEntry file, DiffContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Рабочие деревья репозитория, к которому относится каталог, — чтобы из панели можно было
    /// перейти к копии, в которой работает сабагент. Сбой — пустой список, не исключение.
    /// </summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken);
}

/// <summary>Diff не построен: git недоступен, каталог не в репозитории, базы нет и так далее.</summary>
public sealed class DiffUnavailableException : Exception
{
    /// <inheritdoc cref="DiffUnavailableException" />
    /// <param name="failure">Причина.</param>
    /// <param name="message">Текст для панели — пользователь видит его как есть.</param>
    public DiffUnavailableException(DiffFailure failure, string message)
        : base(message) => Failure = failure;

    /// <inheritdoc cref="DiffUnavailableException(DiffFailure, string)" />
    /// <param name="failure">Причина.</param>
    /// <param name="message">Текст для панели.</param>
    /// <param name="innerException">Исходный сбой.</param>
    public DiffUnavailableException(DiffFailure failure, string message, Exception innerException)
        : base(message, innerException) => Failure = failure;

    /// <summary>Причина.</summary>
    public DiffFailure Failure { get; }
}
