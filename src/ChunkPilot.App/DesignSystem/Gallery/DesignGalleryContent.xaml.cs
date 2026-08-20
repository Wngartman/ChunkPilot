using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.DesignSystem.Gallery;

/// <summary>
/// The Design Gallery body. DEVELOPMENT ONLY.
/// </summary>
/// <remarks>
/// Split from the window so it can be measured and rendered to a bitmap at an exact width without
/// showing a window, which is what makes the deterministic Wide/Standard/Compact capture possible.
/// </remarks>
public partial class DesignGalleryContent : UserControl
{
    public DesignGalleryContent()
    {
        NoOpCommand = new RelayCommand(() => { });
        InitializeComponent();
    }

    /// <summary>
    /// Satisfies components that only reveal an affordance when a command is present, without the
    /// gallery pretending to perform an operation.
    /// </summary>
    public ICommand NoOpCommand { get; }
}
