namespace ChunkPilot.App;

public partial class InstallServerWindow : Window
{
    public InstallServerWindow(InstallServerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is InstallServerViewModel { IsInstalling: true })
            return;
        DialogResult = DataContext is InstallServerViewModel { Result: not null };
        Close();
    }
}
