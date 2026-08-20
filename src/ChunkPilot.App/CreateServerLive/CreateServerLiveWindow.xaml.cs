using System.Windows.Input;
using System.Windows.Threading;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>
/// Host window for the live Vanilla creation wizard. DEVELOPMENT-GATED.
/// </summary>
/// <remarks>
/// <para>
/// Owns focus movement, the clipboard and closing. The step sequence, validation, the plan and the
/// operation belong to <see cref="LiveVanillaWizardViewModel"/>.
/// </para>
/// <para>
/// Closing while a creation is running is deliberately not blocked and deliberately does not cancel
/// anything: the Agent owns the operation, carries on without the window, and a reopened wizard
/// reattaches to it. Blocking the close would be the modal confirmation the close rules prohibit, and
/// cancelling on close would throw away a download because somebody clicked the wrong X.
/// </para>
/// </remarks>
public partial class CreateServerLiveWindow : Window
{
    private readonly LiveVanillaWizardViewModel viewModel;

    public CreateServerLiveWindow(LiveVanillaWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.FocusRequested += OnFocusRequested;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.CopyRequested += OnCopyRequested;
        // ContentRendered rather than Loaded: the window has a real handle and something drawn in it
        // by then, so raising it cannot flash an empty frame.
        ContentRendered += (_, _) =>
        {
            PresentInForeground();
            MoveFocusTo(viewModel.CurrentStep);
        };
        Closed += (_, _) =>
        {
            viewModel.FocusRequested -= OnFocusRequested;
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.CopyRequested -= OnCopyRequested;
            viewModel.Dispose();
        };
    }

    /// <summary>The view model this window is showing. Exposed for runtime inspection and tests.</summary>
    public LiveVanillaWizardViewModel ViewModel => viewModel;

    /// <summary>
    /// Brings the wizard to the front once, at the moment it is first presented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is opened by a command-line switch, so the process that started ChunkPilot — a
    /// terminal, usually — still owns the foreground. Windows refuses to let a process that does not
    /// own the foreground take it, and <see cref="Window.Activate"/> alone therefore only flashes the
    /// taskbar button. Briefly marking the window topmost is the supported way to raise it without
    /// requesting foreground rights: the flag is cleared in the same call, so nothing stays pinned
    /// above other applications.
    /// </para>
    /// <para>
    /// Called once, from the first render. Nothing calls it again, so a provider refresh or a running
    /// creation can never pull the user away from whatever they moved on to.
    /// </para>
    /// </remarks>
    public void PresentInForeground()
    {
        if (presented)
            return;
        presented = true;

        WindowForegroundPresenter.Present(this);
    }

    private bool presented;

    private void OnCloseRequested(object? sender, EventArgs args) => Close();

    private void OnFocusRequested(object? sender, CreationWizardStep step) => MoveFocusTo(step);

    private void OnCopyRequested(object? sender, string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process can hold the clipboard open. Failing to copy a diagnostic is not worth
            // an error dialog on top of whatever the user is already dealing with.
        }
    }

    /// <summary>
    /// Escape closes the window unless a creation is running.
    /// </summary>
    /// <remarks>
    /// While an operation is in flight, Escape is ignored: a stray key press should not dismiss the
    /// only surface showing what is happening. The window's own close button still works, and closing
    /// still does not cancel the operation.
    /// </remarks>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || e.Handled || viewModel.IsOperationRunning)
            return;
        e.Handled = true;
        Close();
    }

    /// <summary>
    /// Moves focus to the first meaningful control of a step, once the step's content is visible.
    /// </summary>
    /// <remarks>
    /// Queued at input priority because the step's panel is still collapsed while the property change
    /// is being handled, and a collapsed element cannot take focus. Review lands programmatically on
    /// its heading (which is not a tab stop), so focus cannot undo the explicit scroll-to-top reset.
    /// Normal tab order still reaches the EULA controls before the disabled Create action.
    /// </remarks>
    private void MoveFocusTo(CreationWizardStep step) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            WizardScroller.ScrollToTop();
            WizardScroller.UpdateLayout();
            IInputElement? target = step switch
            {
                CreationWizardStep.Intent => IntentList,
                CreationWizardStep.Setup => ServerNameBox,
                CreationWizardStep.Review => ReviewHeading,
                CreationWizardStep.Creating => StopButton,
                CreationWizardStep.Completion => OpenServerButton.IsVisible ? OpenServerButton : CloseButton,
                _ => null
            };
            target?.Focus();
        });
}
