using System.Globalization;
using System.Text;
using ClaudeAgentsShell.Application.Ports;

namespace ClaudeAgentsShell.Sessions.Storage;

/// <summary>
/// Журнал сбоев в каталоге данных приложения (<c>crash.log</c>). В каталог проекта
/// пользователя и в <c>~/.claude</c> не пишется ничего (раздел 7 CLAUDE.md).
/// <para>
/// Файл дописывается, а при переполнении переезжает в <c>crash.log.1</c>: на диске
/// остаётся не больше двух файлов, то есть примерно мегабайт. Ротация вместо обрезания
/// начала — запись о первом сбое в сессии обычно и есть самая ценная, и терять её ради
/// более свежих не хочется.
/// </para>
/// </summary>
public sealed class CrashLog : ICrashLog
{
    /// <summary>Имя файла журнала в каталоге данных приложения.</summary>
    public const string FileName = "crash.log";

    /// <summary>Имя предыдущего файла журнала, в который уезжает переполненный.</summary>
    public const string PreviousFileName = "crash.log.1";

    /// <summary>Потолок размера файла: при превышении следующая запись начинает новый файл.</summary>
    public const long MaxBytes = 512 * 1024;

    // Предел глубины обхода цепочки причин: у испорченного исключения InnerException
    // может замкнуться на себя, и обход без предела завис бы прямо в обработчике падения.
    private const int MaxDepth = 8;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAppDataPaths _paths;
    private readonly TimeProvider _time;

    // Сбой приходит с любого потока: обработчик домена и необслуженная задача живут
    // в пуле. Замок держит записи целыми, а не перемешанными построчно.
    private readonly object _gate = new();

    /// <inheritdoc cref="CrashLog" />
    public CrashLog(IAppDataPaths paths, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        _paths = paths;
        _time = time;
    }

    /// <inheritdoc />
    public string? Write(string source, Exception exception)
    {
        try
        {
            // Проверка вместо ArgumentNullException: сигнатура запрещает null, но зовут
            // отсюда обработчики падения, и бросить в ответ на неверный вызов значит
            // сделать ровно то, от чего эта служба защищает.
            if (exception is null)
            {
                return null;
            }

            // Обращение к AppData создаёт каталог данных, если его ещё нет.
            var path = Path.Combine(_paths.AppData, FileName);
            var entry = Format(source, exception, _time.GetLocalNow());

            lock (_gate)
            {
                RotateIfFull(path);
                File.AppendAllText(path, entry, Utf8);
            }

            return path;
        }
        catch
        {
            // Единственный уместный пустой catch в проекте. Это журнал сбоев: его зовут
            // из обработчика уже случившегося падения. Диск может быть заполнен, каталог
            // профиля — недоступен, файл — открыт чужим процессом; в любом из этих случаев
            // исключение отсюда добило бы приложение вместо того, чтобы дать ему жить
            // дальше. Сообщать о сбое записи некуда — сообщать о сбоях и есть эта служба.
            return null;
        }
    }

    /// <summary>
    /// Переполненный файл уезжает в <see cref="PreviousFileName" />. Своя защита от сбоя:
    /// занятый чужим процессом <c>crash.log.1</c> не должен стоить нам самой записи —
    /// лучше файл сверх потолка, чем потерянный стек.
    /// </summary>
    private static void RotateIfFull(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < MaxBytes)
            {
                return;
            }

            File.Move(path, Path.Combine(file.DirectoryName ?? string.Empty, PreviousFileName), overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Format(string source, Exception exception, DateTimeOffset moment)
    {
        var builder = new StringBuilder(1024);
        builder.Append("==== ")
            .Append(moment.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" · ")
            .Append(string.IsNullOrWhiteSpace(source) ? "неизвестный источник" : source)
            .Append(" ====")
            .Append(Environment.NewLine);

        Append(builder, exception, depth: 0, label: null);
        builder.Append(Environment.NewLine);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, Exception exception, int depth, string? label)
    {
        if (label is not null)
        {
            builder.Append("--- ").Append(label).Append(" ---").Append(Environment.NewLine);
        }

        builder.Append(exception.GetType().FullName ?? exception.GetType().Name)
            .Append(": ")
            .Append(exception.Message)
            .Append(Environment.NewLine);

        if (exception.StackTrace is { Length: > 0 } stack)
        {
            builder.Append(stack).Append(Environment.NewLine);
        }
        else
        {
            builder.Append("(стек недоступен)").Append(Environment.NewLine);
        }

        if (depth >= MaxDepth)
        {
            builder.Append("--- цепочка причин обрезана по глубине ---").Append(Environment.NewLine);
            return;
        }

        // У AggregateException важны все причины, а не только первая: необслуженная задача
        // приходит именно им, и второе исключение в нём — тоже потерянный сбой.
        if (exception is AggregateException aggregate)
        {
            for (var i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                Append(builder, aggregate.InnerExceptions[i], depth + 1, $"Причина {i + 1}");
            }

            return;
        }

        if (exception.InnerException is { } inner)
        {
            Append(builder, inner, depth + 1, "Внутреннее исключение");
        }
    }
}
