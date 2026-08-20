using System.Windows.Input;
using System.Windows.Threading;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// Host window for the Create Server v2 preview. PREVIEW ONLY.
/// </summary>
/// <remarks>
/// Owns focus movement and closing. Everything else - the step sequence, validation and the review -
/// belongs to <see cref="CreateServerPreviewViewModel"/>, which holds no agent, installer or store
/// and therefore cannot install anything.
/// </remarks>
public partial class CreateServerPreviewWindow : Window
{
    private readonly CreateServerPreviewViewModel viewModel;

    public CreateServerPreviewWindow(CreateServerPreviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.FocusRequested += OnFocusRequested;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += (_, _) => MoveFocusTo(viewModel.CurrentStep);
        Closed += (_, _) =>
        {
            viewModel.FocusRequested -= OnFocusRequested;
            viewModel.CloseRequested -= OnCloseRequested;
        };
    }

    private void OnCloseRequested(object? sender, EventArgs args) => Close();

    private void OnFocusRequested(object? sender, CreationWizardStep step) => MoveFocusTo(step);

    /// <summary>
    /// Escape closes the preview, matching every other bounded task window in ChunkPilot.
    /// </summary>
    /// <remarks>
    /// Nothing is in progress and nothing has been written, so closing needs no confirmation and
    /// cannot lose work.
    /// </remarks>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || e.Handled)
            return;
        e.Handled = true;
        Close();
    }

    /// <summary>
    /// Moves focus to the first meaningful control of a step, once the step's content is visible.
    /// </summary>
    /// <remarks>
    /// Queued at input priority because the step's panel is still collapsed while the property change
    /// is being handled, and a collapsed element cannot take focus. On Review and Completion the
    /// destination is the primary action, which is side-effect free and therefore the safe default.
    /// </remarks>
    private void MoveFocusTo(CreationWizardStep step) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            IInputElement? target = step switch
            {
                CreationWizardStep.Intent => IntentList,
                CreationWizardStep.Setup => ServerNameBox,
                CreationWizardStep.Review => FinishButton,
                CreationWizardStep.Completion => CloseButton,
                _ => null
            };
            target?.Focus();
        });

    /// <summary>The view model this window is showing. Exposed for runtime inspection and tests.</summary>
    public CreateServerPreviewViewModel ViewModel => viewModel;
}
