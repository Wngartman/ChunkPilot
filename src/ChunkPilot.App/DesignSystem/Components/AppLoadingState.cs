namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Placeholder shown while a surface is genuinely waiting on data.
/// </summary>
/// <remarks>
/// Anatomy: busy indicator, what is being loaded, optional detail. Loading is distinct from empty
/// and from unavailable, and the copy must say which one it is. Under Reduced Motion the busy
/// indicator stops animating and the text alone carries the state, so this component keeps working
/// with animation switched off.
/// </remarks>
public sealed class AppLoadingState : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppLoadingState), new PropertyMetadata("Loading…"));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AppLoadingState), new PropertyMetadata(string.Empty));

    /// <summary>What is being loaded, in plain language.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Optional detail, such as the source being contacted.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
