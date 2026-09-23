using ClaudeAgentsShell.App.History;
using ClaudeAgentsShell.App.Services;
using ClaudeAgentsShell.Sessions;
using ClaudeAgentsShell.Sessions.History;
using ClaudeAgentsShell.Sessions.Storage;
using ClaudeAgentsShell.Tests.Sessions;
using Xunit;

namespace ClaudeAgentsShell.Tests.App;

/// <summary>
/// Окно истории нового проекта (приёмка M3, п. 7): каталога <c>~/.claude/projects/&lt;slug&gt;</c>
/// ещё нет. Порты настоящие — читатель и наблюдатель, ждущий появления каталога, — чтобы
/// заглушку проверял их действительный ответ, а не подделка.
/// </summary>
public sealed class HistoryEmptyProjectTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_project_without_history_directory_shows_the_empty_stub(bool projectsRootExists)
    {
        using var temp = new TempDirectory();
        var projectsRoot = temp.Combine("claude", "projects");
        if (projectsRootExists)
        {
            Directory.CreateDirectory(projectsRoot);
        }

        var paths = new AppDataPaths(temp.Combine("appdata"), projectsRoot);
        var reader = new SessionHistoryReader(paths, new SessionsOptions());
        var watcher = new SessionHistoryWatcher(paths, TimeProvider.System);
        var project = new SessionHistoryProject(Guid.NewGuid(), "fresh", temp.Combine("work", "fresh"));
        var dispatcher = new QueuedUiDispatcher();
        using var vm = new SessionHistoryViewModel(
            new SessionHistoryRequest(project, new HashSet<string>()), reader, watcher, dispatcher, TimeProvider.System);

        await vm.LoadAsync(CancellationToken.None);
        dispatcher.Drain();

        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Rows);
        Assert.Null(vm.SelectedRow);
        Assert.Equal("В этом проекте ещё нет сессий", vm.EmptyText);
        Assert.Equal("~/.claude/projects", vm.FooterStatus);
        Assert.False(vm.AcceptCommand.CanExecute(null));
        Assert.False(Directory.Exists(Path.Combine(projectsRoot, SessionSlug.From(project.WorkingDirectory))));
    }
}
