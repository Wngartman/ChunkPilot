namespace ChunkPilot.App;

public partial class ImportServerWindow : Window
{
    public ImportServerWindow(ImportServerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportServerViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.Name) ||
            string.IsNullOrWhiteSpace(viewModel.Executable) ||
            string.IsNullOrWhiteSpace(viewModel.WorkingDirectory))
        {
            MessageBox.Show("Name, executable, and working directory are required.",
                "Incomplete launch profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

