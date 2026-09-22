using System.Globalization;
using System.Text;
using System.Threading.Channels;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Sessions.Hooks;

/// <summary>
/// Журнал принятых хуков в каталоге данных приложения (<c>hooks.log</c>), рядом с
/// <c>hook-send.cmd</c> и журналом сбоев. В проект пользователя не пишется ничего.
/// <para>
/// Строка формируется в потоке приёма, а на диск уходит из отдельной задачи через
/// ограниченную очередь: поток приёма хуков диска не ждёт. Переполненная очередь теряет
/// новые строки, а не блокирует приём — журнал диагностический, событие для вкладки
/// дороже строки о нём.
/// </para>
/// <para>
/// При переполнении файл переезжает в <c>hooks.log.1</c>: на диске не больше двух файлов.
/// Сбой ввода-вывода теряет пачку строк и больше ничего — ни исключения наружу, ни
/// остановки записи следующих пачек.
/// </para>
/// </summary>
public sealed class HookLog : IHookLog, IAsyncDisposable
{
    /// <summary>Имя файла журнала в каталоге данных приложения.</summary>
    public const string FileName = "hooks.log";

    /// <summary>Имя предыдущего файла журнала, в который уезжает переполненный.</summary>
    public const string PreviousFileName = "hooks.log.1";

    /// <summary>Потолок размера файла: при превышении следующая пачка начинает новый файл.</summary>
    public const long MaxBytes = 1024 * 1024;

    // Сколько символов токена попадает в журнал: достаточно, чтобы различить вкладки,
    // и недостаточно, чтобы по журналу подделать хук чужой вкладки.
    private const int TokenPrefixLength = 8;

    // Потолок длины одного поля: полезная нагрузка приходит извне, и строка журнала
    // не должна раздуваться от чужого мусора.
    private const int MaxFieldLength = 128;

    private const int QueueCapacity = 4096;

    // Выход приложения ждёт дописывания не дольше этого: зависший диск не должен
    // держать закрытие окна.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAppDataPaths _paths;
    private readonly TimeProvider _time;
    private readonly Channel<string> _queue;
    private readonly Task _writer;

    /// <inheritdoc cref="HookLog" />
    public HookLog(IAppDataPaths paths, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        _paths = paths;
        _time = time;

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        // Путь к каталогу данных не читается здесь: его геттер создаёт каталог, и сбой
        // в конструкторе уронил бы вместе с журналом приёмник хуков.
        _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Record(HookEvent hookEvent)
    {
        if (hookEvent is null)
        {
            return;
        }

        try
        {
            _queue.Writer.TryWrite(Format(hookEvent));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // Поля пришли извне; строка журнала не стоит сбоя в потоке приёма.
        }
    }

    /// <summary>
    /// Дописывает очередь и останавливает запись. Ждёт не дольше пары секунд: выход
    /// приложения зовёт это синхронно, и зависший диск не должен его держать.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        try
        {
            await _writer.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Диск не ответил вовремя — недописанные строки теряются, выход не ждёт.
        }
    }

    /// <summary>Строка журнала: время, хук, source, session_id, agent_id, фоновые задачи, начало токена.</summary>
    private string Format(HookEvent hookEvent)
    {
        var moment = TimeZoneInfo.ConvertTime(hookEvent.ReceivedUtc, _time.LocalTimeZone);

        var builder = new StringBuilder(160);
        builder.Append(moment.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(hookEvent.Kind.ToString())
            .Append(" source=").Append(Field(hookEvent.Source))
            .Append(" session=").Append(Field(hookEvent.SessionId))
            .Append(" agent=").Append(Field(hookEvent.AgentId))
            .Append(" bg=");

        AppendBackgroundTasks(builder, hookEvent.BackgroundTasks);

        builder.Append(" token=").Append(Field(TokenPrefix(hookEvent.CorrelationToken)))
            .Append(Environment.NewLine);

        return builder.ToString();
    }

    private static void AppendBackgroundTasks(StringBuilder builder, IReadOnlyList<BackgroundTask>? tasks)
    {
        // Поля нет и поле пустое — разные случаи (см. HookEvent.BackgroundTasks), и журнал
        // их различает: «-» против «0».
        if (tasks is null)
        {
            builder.Append('-');
            return;
        }

        builder.Append(tasks.Count.ToString(CultureInfo.InvariantCulture));
        if (tasks.Count == 0)
        {
            return;
        }

        builder.Append('[');
        for (var i = 0; i < tasks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Field(tasks[i].Type));
        }

        builder.Append(']');
    }

    private static string? TokenPrefix(string? token) =>
        token is { Length: > TokenPrefixLength } ? token[..TokenPrefixLength] : token;

    /// <summary>
    /// Поле из полезной нагрузки в безопасном для журнала виде: без переводов строк
    /// и пробелов (одна строка на хук, поля разделены пробелом) и не длиннее потолка.
    /// </summary>
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var length = Math.Min(value.Length, MaxFieldLength);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        return builder.ToString();
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new StringBuilder();

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Всё, что накопилось, уходит одной записью: пачка хуков сабагентов не должна
            // стоить открытия файла на каждую строку.
            batch.Clear();
            while (reader.TryRead(out var line))
            {
                batch.Append(line);
            }

            Append(batch.ToString());
        }
    }

    private void Append(string text)
    {
        try
        {
            // Обращение к AppData создаёт каталог данных, если его ещё нет.
            var path = Path.Combine(_paths.AppData, FileName);
            RotateIfFull(path);
            File.AppendAllText(path, text, Utf8);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or NotSupportedException
                                              or ArgumentException)
        {
            // Диск заполнен, файл занят чужим процессом, профиль недоступен: пачка теряется,
            // приёмник хуков и следующие пачки живут дальше.
        }
    }

    /// <summary>
    /// Переполненный файл уезжает в <see cref="PreviousFileName" />. Своя защита от сбоя:
    /// занятый чужим процессом <c>hooks.log.1</c> не должен стоить самой записи —
    /// лучше файл сверх потолка, чем потерянные строки.
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
}
