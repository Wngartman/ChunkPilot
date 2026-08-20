namespace ChunkPilot.App;

public partial class ServerOverviewPage : System.Windows.Controls.UserControl
{
    public ServerOverviewPage() => InitializeComponent();

    private void PlayerMenuButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
