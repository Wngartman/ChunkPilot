using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ChunkPilot.App;

/// <summary>
/// A compact borderless transparent splash that shows only the ChunkPilot icon
/// centered on screen while the application initializes.
/// </summary>
/// <remarks>
/// <para>When Reduced Motion is active, the pulse animation is stopped after load.</para>
/// <para>For startup failure, the main window shows a themed recovery state.</para>
/// </remarks>
public partial class SplashWindow : Window
{
    private readonly bool _reducedMotion;

    public SplashWindow(bool reducedMotion = false)
    {
        InitializeComponent();
        _reducedMotion = reducedMotion;

        Loaded += (_, _) =>
        {
            // Stop animation under Reduced Motion
            if (_reducedMotion)
            {
                var storyboards = new List<Storyboard>();
                foreach (var trigger in Triggers)
                {
                    if (trigger is EventTrigger et)
                        foreach (var action in et.Actions)
                            if (action is BeginStoryboard bs)
                                storyboards.Add(bs.Storyboard);
                }
                foreach (var sb in storyboards)
                    sb.Stop(this);
            }
        };
    }

    /// <summary>Cleanly close the splash (called when main window is ready).</summary>
    public void CloseSplash()
    {
        Close();
    }
}
