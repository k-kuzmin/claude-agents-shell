using Microsoft.Win32;

namespace ClaudeAgentsShell.App.Services;

/// <summary>Штатный диалог выбора папки WPF (.NET 8). Единственное место, где приложение его открывает.</summary>
public sealed class OpenFolderDialogPicker : IFolderPicker
{
    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
