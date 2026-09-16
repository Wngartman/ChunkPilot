using System.Windows.Input;
using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.App;

/// <summary>The shared, owner-modal native message surface. Only an explicit action confirms.</summary>
public partial class MessageDialogWindow : Window
{
    private readonly bool confirmation;

    public MessageDialogWindow(string title, string message, bool confirmation, AppIconKind icon)
    {
        this.confirmation = confirmation;
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        MessageText.Text = message;
        MessageIcon.Kind = icon;
        // Respect the available desktop at Windows scaling without inventing another layout token.
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        ConfirmButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = title switch
        {
            "Restore backup" => "Restore backup",
            "Delete backup" => "Delete backup",
            "Roll back server version" => "Roll back",
            _ => "Continue"
        };
        CancelButton.Content = confirmation ? "Cancel" : "Close";
        FocusManager.SetFocusedElement(this, CancelButton);
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (confirmation) DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }
}
