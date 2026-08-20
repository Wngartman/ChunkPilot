using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>
/// Constructs the live Vanilla creation wizard used by the product and retained development shortcut.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// ChunkPilot.exe --create-server-v2-live-vanilla
/// </code>
/// </para>
/// <para>
/// This is a different kind of switch from <c>--create-server-v2-preview</c>, and the difference
/// matters. The preview replaces startup: it takes no single-instance lock, starts no agent, opens no
/// database and shows invented data, so reviewing it cannot disturb anything. This one does the
/// opposite — it runs the whole normal startup first and then opens one extra window on top of the
/// real shell, because a wizard that genuinely creates a server needs the Agent that owns the work,
/// the database the server is registered in, and the navigation the finished server is opened
/// through. There is no way to have those and also be side-effect free, so this switch is honest
/// about being the real thing.
/// </para>
/// <para>
/// The switch is only a shortcut. Normal beginner <b>Create server</b> actions enter this same live
/// workflow through the shell's semantic Vanilla creation request; the synthetic preview remains
/// isolated behind its separate preview switch.
/// </para>
/// </remarks>
public static class CreateServerLiveLauncher
{
    /// <summary>The one switch that opens the live Vanilla wizard.</summary>
    public const string LiveVanillaSwitch = "--create-server-v2-live-vanilla";

    /// <summary>True when the arguments ask for the live wizard.</summary>
    public static bool IsRequested(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, LiveVanillaSwitch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opens the wizard over the running shell, reattaching to work already in progress.
    /// </summary>
    /// <param name="owner">The shell window, so the wizard behaves as one of its dialogs.</param>
    /// <param name="gateway">The narrow Agent surface the wizard is allowed to use.</param>
    /// <param name="navigator">How a finished server is opened. The wizard never navigates itself.</param>
    /// <param name="locations">
    /// How the user chooses a different folder. Omitting it hides the action rather than offering
    /// one that does nothing.
    /// </param>
    public static CreateServerLiveWindow Open(
        Window? owner,
        IVanillaCreationGateway gateway,
        ICreatedServerNavigator? navigator,
        IServerLocationChooser? locations = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var viewModel = new LiveVanillaWizardViewModel(gateway, navigator: navigator, locationChooser: locations);
        var window = new CreateServerLiveWindow(viewModel) { Owner = owner };
        AppTheme.Attach(window);
        window.Show();
        // Raise once so an explicitly requested creation workflow is visible even when another
        // window currently owns the foreground. The presenter restores the prior Topmost value.
        window.PresentInForeground();
        // An operation the Agent is still running is shown rather than hidden behind a fresh first
        // step. Nothing is started by asking.
        _ = viewModel.TryReattachAsync();
        return window;
    }
}
