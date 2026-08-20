namespace ChunkPilot.App.DesignSystem.Gallery;

/// <summary>
/// Host window for the Design Gallery. DEVELOPMENT ONLY.
/// </summary>
public partial class DesignGalleryWindow : Window
{
    public DesignGalleryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyPreviewState();
    }

    private void PreviewStateChanged(object sender, RoutedEventArgs e) => ApplyPreviewState();

    private void LayoutModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        // "Follows window" hands control back to the responsive behaviour on the window; the fixed
        // options pin the mode on the body so a reviewer can compare all three at one window size.
        if (LayoutFollowsWindow.IsChecked == true)
        {
            GalleryBody.ClearValue(AppLayout.ModeProperty);
            return;
        }

        var mode = LayoutCompact.IsChecked == true
            ? AppLayoutMode.Compact
            : LayoutStandard.IsChecked == true
                ? AppLayoutMode.Standard
                : AppLayoutMode.Wide;
        AppLayout.SetMode(GalleryBody, mode);
    }

    private void ApplyPreviewState() =>
        AppTheme.ApplyPreview(
            this,
            highContrast: HighContrastToggle.IsChecked == true,
            motionEnabled: ReducedMotionToggle.IsChecked != true);
}
