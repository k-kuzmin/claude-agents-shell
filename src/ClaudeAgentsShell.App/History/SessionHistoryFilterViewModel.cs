using ClaudeAgentsShell.App.ViewModels;

namespace ClaudeAgentsShell.App.History;

/// <summary>Вариант фильтра окна истории: один проект либо «все проекты».</summary>
public sealed class SessionHistoryFilterViewModel : ObservableObject
{
    private bool _isSelected;

    /// <inheritdoc cref="SessionHistoryFilterViewModel" />
    /// <param name="projectId">Проект фильтра; <c>null</c> — «все проекты».</param>
    /// <param name="label">Подпись на кнопке.</param>
    public SessionHistoryFilterViewModel(Guid? projectId, string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        ProjectId = projectId;
        Label = label;
    }

    /// <summary>Проект фильтра; <c>null</c> — «все проекты».</summary>
    public Guid? ProjectId { get; }

    /// <summary>Подпись на кнопке фильтра.</summary>
    public string Label { get; }

    /// <summary>Фильтр «все проекты».</summary>
    public bool IsAllProjects => ProjectId is null;

    /// <summary>Фильтр выбран сейчас.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
