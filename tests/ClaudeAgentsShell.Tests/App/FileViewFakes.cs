using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Файлы рабочего каталога без диска: корень и ответы задаёт тест.</summary>
internal sealed class FakeWorkspaceFileReader : IWorkspaceFileReader
{
    private readonly object _sync = new();
    private readonly List<(string Root, string Directory, ShowFileItem Item)> _reads = [];
    private int _inFlight;

    /// <summary>Корень для любого каталога; <c>null</c> — каталога нет.</summary>
    public string? Root { get; set; } = @"D:\src\alpha";

    /// <summary>Ответ на чтение; по умолчанию — короткий текст с путём файла.</summary>
    public Func<ShowFileItem, CancellationToken, Task<ViewedFile>>? Read { get; set; }

    public List<string> RootRequests { get; } = [];

    public IReadOnlyList<(string Root, string Directory, ShowFileItem Item)> Reads
    {
        get
        {
            lock (_sync)
            {
                return [.. _reads];
            }
        }
    }

    /// <summary>Наибольшее число одновременных чтений.</summary>
    public int MaxInFlight { get; private set; }

    public static ViewedFile Text(ShowFileItem item, string text) =>
        new(item.Path, text, item.Focus, ViewedFileProblem.None);

    public static ViewedFile Problem(ShowFileItem item, ViewedFileProblem problem) =>
        new(item.Path, null, item.Focus, problem);

    public Task<string?> ResolveRootAsync(string directory, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            RootRequests.Add(directory);
        }

        return Task.FromResult(Root);
    }

    public async Task<ViewedFile> ReadAsync(string root, string directory, ShowFileItem item, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _reads.Add((root, directory, item));
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
        }

        try
        {
            return Read is { } answer
                ? await answer(item, cancellationToken)
                : Text(item, "// " + item.Path);
        }
        finally
        {
            lock (_sync)
            {
                _inFlight--;
            }
        }
    }
}

/// <summary>Панель в режиме «файл» без страницы: записывает показы.</summary>
internal sealed class FakeFileView : IFileView
{
    private readonly object _sync = new();
    private readonly List<(TerminalId TerminalId, FileViewSet Files)> _shown = [];

    public IReadOnlyList<(TerminalId TerminalId, FileViewSet Files)> Shown
    {
        get
        {
            lock (_sync)
            {
                return [.. _shown];
            }
        }
    }

    /// <summary>Вмешательство в показ: бросить или «повиснуть».</summary>
    public Func<CancellationToken, ValueTask>? OnShow { get; set; }

    public ValueTask ShowFilesAsync(TerminalId terminalId, FileViewSet files, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _shown.Add((terminalId, files));
        }

        return OnShow?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }
}
