namespace ChunkPilot.App.DesignSystem;

/// <summary>Width-driven layout modes. These are layout modes, never separate feature sets.</summary>
public enum AppLayoutMode
{
    /// <summary>Below the standard breakpoint. Navigation collapses, action groups stack.</summary>
    Compact,

    /// <summary>Between the breakpoints. Persistent navigation, one primary content column.</summary>
    Standard,

    /// <summary>At or above the wide breakpoint. Persistent navigation plus secondary columns.</summary>
    Wide
}

/// <summary>
/// Responsive layout state, published to the whole element tree as an inherited attached property.
/// </summary>
/// <remarks>
/// <para>
/// Set <c>AppLayout.IsResponsive="True"</c> on a window and every descendant - including control
/// templates - can react with a trigger on <c>AppLayout.Mode</c>. No page needs a SizeChanged
/// handler, and no page invents its own breakpoints, which is how the previous shell ended up with
/// thresholds that disagreed with the documented ones.
/// </para>
/// <para>Breakpoints come from <c>AppBreakpointStandard</c> and <c>AppBreakpointWide</c>.</para>
/// </remarks>
public static class AppLayout
{
    private const double FallbackStandardBreakpoint = 900d;
    private const double FallbackWideBreakpoint = 1280d;

    /// <summary>The layout mode in effect for this element and its descendants.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(AppLayoutMode), typeof(AppLayout),
        new FrameworkPropertyMetadata(AppLayoutMode.Standard, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>When true, the element keeps <see cref="ModeProperty"/> in step with its own width.</summary>
    public static readonly DependencyProperty IsResponsiveProperty = DependencyProperty.RegisterAttached(
        "IsResponsive", typeof(bool), typeof(AppLayout),
        new PropertyMetadata(false, OnIsResponsiveChanged));

    public static AppLayoutMode GetMode(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (AppLayoutMode)element.GetValue(ModeProperty);
    }

    public static void SetMode(DependencyObject element, AppLayoutMode value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ModeProperty, value);
    }

    public static bool GetIsResponsive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsResponsiveProperty);
    }

    public static void SetIsResponsive(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsResponsiveProperty, value);
    }

    /// <summary>Classifies a width using the breakpoints declared in the token layer.</summary>
    /// <param name="width">Available width in device-independent pixels.</param>
    /// <param name="scope">Element used to resolve breakpoint tokens; may be null in tests.</param>
    public static AppLayoutMode ModeForWidth(double width, FrameworkElement? scope = null)
    {
        var standard = ResolveBreakpoint(scope, "AppBreakpointStandard", FallbackStandardBreakpoint);
        var wide = ResolveBreakpoint(scope, "AppBreakpointWide", FallbackWideBreakpoint);
        if (double.IsNaN(width) || width <= 0)
            return AppLayoutMode.Standard;
        if (width >= wide)
            return AppLayoutMode.Wide;
        return width >= standard ? AppLayoutMode.Standard : AppLayoutMode.Compact;
    }

    private static double ResolveBreakpoint(FrameworkElement? scope, string key, double fallback)
    {
        var value = scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
        return value is double resolved && resolved > 0 ? resolved : fallback;
    }

    private static void OnIsResponsiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element)
            return;
        element.SizeChanged -= OnScopeSizeChanged;
        element.Loaded -= OnScopeLoaded;
        if (args.NewValue is not true)
            return;
        element.SizeChanged += OnScopeSizeChanged;
        element.Loaded += OnScopeLoaded;
        Refresh(element);
    }

    private static void OnScopeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
            Refresh(element);
    }

    private static void OnScopeSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged || sender is not FrameworkElement element)
            return;
        Refresh(element);
    }

    private static void Refresh(FrameworkElement element)
    {
        var width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        var mode = ModeForWidth(width, element);
        if (GetMode(element) != mode)
            SetMode(element, mode);
    }
}
