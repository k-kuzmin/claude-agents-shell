using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.App.State;
using ClaudeAgentsShell.Application.Ports;
using ClaudeAgentsShell.Domain;
using ClaudeAgentsShell.Tests.Fakes;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>Раскладка в памяти вместо layout.json. Запоминает каждую запись.</summary>
internal sealed class FakeLayoutStore : ILayoutStore
{
    /// <summary>Что вернёт чтение.</summary>
    public WorkspaceLayout Stored { get; set; } = WorkspaceLayout.Empty;

    /// <summary>Все записи по порядку.</summary>
    public List<WorkspaceLayout> Saves { get; } = [];

    /// <summary>Исключение, которым ответит следующая запись.</summary>
    public Exception? SaveFailure { get; set; }

    public WorkspaceLayout? LastSaved => Saves.Count == 0 ? null : Saves[^1];

    public Task<WorkspaceLayout> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(WorkspaceLayout layout, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            SaveFailure = null;
            return Task.FromException(failure);
        }

        Saves.Add(layout);
        Stored = layout;
        return Task.CompletedTask;
    }

    /// <summary>Регистратор поверх этого хранилища; время по умолчанию стоит на месте.</summary>
    public LayoutRecorder CreateRecorder(TimeProvider? time = null, IUiDispatcher? dispatcher = null) =>
        new(this, time ?? new ManualTimeProvider(), dispatcher ?? new InlineUiDispatcher(), new FakeCrashLog());

    /// <summary>Сервис раскладки поверх этого хранилища и нового регистратора.</summary>
    public WorkspaceLayoutService CreateService(TimeProvider? time = null) => new(this, CreateRecorder(time));
}

/// <summary>Набор терминалов, который запоминает режим запуска каждой вкладки.</summary>
internal sealed class LaunchRecordingWorkspace : ITerminalWorkspace
{
    public FakeTerminalWorkspace Inner { get; } = new();

    /// <summary>Запуски по порядку: проект и режим.</summary>
    public List<(Guid ProjectId, SessionLaunch Launch)> Launches { get; } = [];

    /// <summary>Выполняется на каждом запуске до его исполнения — например, ход часов.</summary>
    public Action? OnOpen { get; set; }

    public event EventHandler<TerminalExitedEventArgs>? TerminalExited
    {
        add => Inner.TerminalExited += value;
        remove => Inner.TerminalExited -= value;
    }

    public IReadOnlyList<TerminalId> Terminals => Inner.Terminals;

    public Task StartAsync(CancellationToken cancellationToken) => Inner.StartAsync(cancellationToken);

    public Task<TerminalId> OpenAsync(ProjectDefinition project, SessionLaunch launch, CancellationToken cancellationToken)
    {
        Launches.Add((project.Id, launch));
        OnOpen?.Invoke();
        return Inner.OpenAsync(project, launch, cancellationToken);
    }

    public Task ActivateAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        Inner.ActivateAsync(terminalId, cancellationToken);

    public Task CloseAsync(TerminalId terminalId, CancellationToken cancellationToken) =>
        Inner.CloseAsync(terminalId, cancellationToken);

    public bool TryResolveTerminal(string? correlationToken, out TerminalId terminalId) =>
        Inner.TryResolveTerminal(correlationToken, out terminalId);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
