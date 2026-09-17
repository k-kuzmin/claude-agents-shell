using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeAgentsShell.App.ViewModels;

/// <summary>
/// Основа ViewModel: уведомление об изменении свойства. Своя, а не из пакета MVVM,
/// потому что от фреймворка нужен ровно этот метод.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Присваивает поле и уведомляет, только если значение действительно изменилось.</summary>
    /// <returns><c>true</c>, если значение изменилось.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    /// <summary>Уведомляет об изменении свойства, значение которого вычисляется.</summary>
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
