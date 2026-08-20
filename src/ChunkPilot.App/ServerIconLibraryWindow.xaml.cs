using ChunkPilot.Core;
using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.App;

public partial class ServerIconLibraryWindow : Window
{
    public string? SelectedPath { get; private set; }

    public ServerIconLibraryWindow(IReadOnlyList<ServerIconLibraryEntry> entries)
    {
        InitializeComponent();
        AppWindowChrome.Apply(this);
        DataContext = entries.Select(entry => new ServerIconLibraryItem(
            entry, ServerIconImageLoader.LoadDetached(entry.Path))).ToArray();
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not ServerIconLibraryItem selected)
            return;
        SelectedPath = selected.Entry.Path;
        DialogResult = true;
    }
}

internal sealed record ServerIconLibraryItem(
    ServerIconLibraryEntry Entry,
    System.Windows.Media.ImageSource? Preview);
