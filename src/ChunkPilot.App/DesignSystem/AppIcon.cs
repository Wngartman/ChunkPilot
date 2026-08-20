using System.Windows.Controls;

namespace ChunkPilot.App.DesignSystem;

/// <summary>Icon size steps. Each step maps to a hand-tuned glyph, not a scaled one.</summary>
public enum AppIconScale
{
    /// <summary>16 dip. Inline with body text, table cells, badges.</summary>
    Small,

    /// <summary>20 dip. Default for buttons, navigation rows and list rows.</summary>
    Medium,

    /// <summary>24 dip. Page headers and prominent single actions.</summary>
    Large,

    /// <summary>32 dip. Empty states only.</summary>
    Hero
}

/// <summary>Icon weight. Filled is reserved for the selected state of a navigation destination.</summary>
public enum AppIconVariant
{
    Regular,
    Filled
}

/// <summary>
/// The only icon element used by ChunkPilot views.
/// </summary>
/// <remarks>
/// <para>
/// Views declare intent (<see cref="Kind"/>) and a size step (<see cref="Scale"/>); the design
/// system owns which glyph, which weight and which pixel size that produces. This is what stops
/// the same concept from appearing as three different glyphs across three pages.
/// </para>
/// <para>
/// Icons are decorative by default: they are not focusable and are hidden from automation, because
/// a shared label or tooltip always carries the meaning. An icon-only control must supply
/// <c>AutomationProperties.Name</c> and a tooltip on the control itself.
/// </para>
/// </remarks>
public sealed class AppIcon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AppIconKind), typeof(AppIcon),
        new FrameworkPropertyMetadata(AppIconKind.Question));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(AppIconScale), typeof(AppIcon),
        new FrameworkPropertyMetadata(AppIconScale.Medium));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(AppIconVariant), typeof(AppIcon),
        new FrameworkPropertyMetadata(AppIconVariant.Regular));

    /// <summary>The semantic meaning of the icon.</summary>
    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The size step. Never set Width/Height on an icon directly.</summary>
    public AppIconScale Scale
    {
        get => (AppIconScale)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>The glyph weight.</summary>
    public AppIconVariant Variant
    {
        get => (AppIconVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <summary>
    /// Keeps icons out of the automation tree entirely.
    /// </summary>
    /// <remarks>
    /// A screen reader announcing "image" between every button label and its text is noise, and it
    /// invites the mistake of putting meaning in an icon that only sighted users receive. The
    /// containing control owns the accessible name.
    /// </remarks>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => null!;
}
