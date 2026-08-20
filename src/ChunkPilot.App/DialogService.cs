using Microsoft.Win32;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public interface IDialogService
{
    string? SelectFolder(string title, string? initialPath = null);
    string? SelectFile(string title, string filter);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
    ServerIconCropSelection? CropServerIcon(string sourcePath) =>
        new(sourcePath, 0, 0, 1);
    string? SelectSavedServerIcon(IReadOnlyList<ServerIconLibraryEntry> entries) => null;
    string? PromptServerDisplayName(string currentName) => null;
}

public sealed record ServerIconCropSelection(string SourcePath, double CropX, double CropY, double CropSize);

public sealed class DialogService : IDialogService
{
    public string? SelectFolder(string title, string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
            InitialDirectory = initialPath is not null && Directory.Exists(initialPath) ? initialPath : null
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? SelectFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public ServerIconCropSelection? CropServerIcon(string sourcePath)
    {
        var window = new ServerIconCropWindow(sourcePath) { Owner = Application.Current.MainWindow };
        AppTheme.Attach(window);
        return window.ShowDialog() == true ? window.Selection : null;
    }

    public string? SelectSavedServerIcon(IReadOnlyList<ServerIconLibraryEntry> entries)
    {
        var window = new ServerIconLibraryWindow(entries) { Owner = Application.Current.MainWindow };
        AppTheme.Attach(window);
        return window.ShowDialog() == true ? window.SelectedPath : null;
    }

    public string? PromptServerDisplayName(string currentName)
    {
        var window = new RenameServerWindow(currentName) { Owner = Application.Current.MainWindow };
        AppTheme.Attach(window);
        return window.ShowDialog() == true ? window.DisplayName : null;
    }
}
