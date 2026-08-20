using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// The single search affordance: leading search glyph, hint text, and a clear control that appears
/// only when there is something to clear.
/// </summary>
/// <remarks>
/// Search filters what is already on screen. It never triggers a network call on keystroke, and the
/// empty result must be handled by an <see cref="AppEmptyState"/> that says the filter matched
/// nothing - not by a blank surface that looks like data loss.
/// </remarks>
public sealed class AppSearchBox : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AppSearchBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(AppSearchBox), new PropertyMetadata("Search"));

    public AppSearchBox() => ClearCommand = new RelayCommand(() => Text = string.Empty);

    /// <summary>The current filter text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Hint describing what is searched, for example "Search servers".</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Bound by the shared template to the clear button.</summary>
    public ICommand ClearCommand { get; }
}
