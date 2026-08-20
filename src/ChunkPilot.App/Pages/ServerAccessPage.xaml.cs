using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ChunkPilot.App;

public partial class ServerAccessPage : UserControl
{
    public ServerAccessPage() => InitializeComponent();

    /// <summary>
    /// Opens a player row's action menu from a left click on its button.
    /// </summary>
    /// <remarks>
    /// A <see cref="ContextMenu"/> is used because it is the themed, keyboard-navigable menu the design
    /// system already provides; WPF only opens it on right click, so an overflow button has to say so
    /// explicitly. Placement is below the button, and the menu inherits the row's data context, which is
    /// what lets each item bind to that player's own commands.
    /// </remarks>
    private void ShowPlayerMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
