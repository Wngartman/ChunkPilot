using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public partial class RenameServerWindow : Window
{
    public string DisplayName { get; private set; } = "";

    public RenameServerWindow(string currentName)
    {
        InitializeComponent();
        AppWindowChrome.Apply(this);
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Validate();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (SaveButton is null || ValidationAlert is null)
            return;
        var problem = CreationNamePolicy.Validate(NameBox.Text)
            .FirstOrDefault(issue => issue.Severity == CreationIssueSeverity.Blocking);
        SaveButton.IsEnabled = problem is null;
        ValidationAlert.Message = problem?.Message ?? "";
        ValidationAlert.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;
        DisplayName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
