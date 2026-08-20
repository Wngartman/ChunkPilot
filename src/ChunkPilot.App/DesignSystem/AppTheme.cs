namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Centralised theme loading and accessibility-preference plumbing for the WPF layer.
/// </summary>
/// <remarks>
/// <para>
/// One entry point, <c>Themes/Theme.xaml</c>, composes the entire design system. Overlay
/// dictionaries for high contrast and Reduced Motion are appended and removed at runtime; because
/// component styles reference brushes and durations with <c>DynamicResource</c>, an overlay change
/// re-styles live windows without a restart.
/// </para>
/// <para>
/// This type owns presentation state only. It never touches the agent, the server lifecycle,
/// persistence or the filesystem.
/// </para>
/// </remarks>
public static class AppTheme
{
    private const string ResourceAssembly = "ChunkPilot";

    /// <summary>Environment override used by the Design Gallery and deterministic capture runs.</summary>
    public const string HighContrastOverrideVariable = "CHUNKPILOT_UI_HIGH_CONTRAST";

    /// <summary>Environment override used by the Design Gallery and deterministic capture runs.</summary>
    public const string ReducedMotionOverrideVariable = "CHUNKPILOT_UI_REDUCED_MOTION";

    private static bool subscribed;

    /// <summary>
    /// The design system, in load order. Mirrors the merged dictionaries in App.xaml, which is what
    /// lets a test or the Design Gallery reproduce the exact same resource tree.
    /// </summary>
    /// <remarks>
    /// Flat by necessity: <c>StaticResource</c> and <c>Style.BasedOn</c> only reliably resolve
    /// against dictionaries merged earlier into the same collection, so these files must stay
    /// siblings rather than being grouped behind a parent dictionary.
    /// </remarks>
    public static IReadOnlyList<string> ThemeDictionaries { get; } =
    [
        "Themes/Tokens/Palette.xaml",
        "Themes/Tokens/ColorTokens.xaml",
        "Themes/Tokens/TypographyTokens.xaml",
        "Themes/Tokens/MetricTokens.xaml",
        "Themes/Tokens/ElevationTokens.xaml",
        "Themes/Tokens/MotionTokens.xaml",
        "Themes/Controls/Internal.xaml",
        "Themes/Controls/Text.xaml",
        "Themes/Controls/Icons.xaml",
        "Themes/Controls/Buttons.xaml",
        "Themes/Controls/Inputs.xaml",
        "Themes/Controls/Selection.xaml",
        "Themes/Controls/Navigation.xaml",
        "Themes/Controls/Surfaces.xaml",
        "Themes/Controls/DataDisplay.xaml",
        "Themes/Controls/Feedback.xaml",
        "Themes/Controls/Overlays.xaml",
        "Themes/Controls/ScrollBars.xaml",
        "Themes/Compatibility/LegacyAliases.xaml"
    ];

    /// <summary>Resolves a design-system dictionary path to its pack URI.</summary>
    /// <remarks>
    /// These are computed rather than cached in static fields on purpose. The <c>pack</c> scheme is
    /// registered by WPF's packaging types, so a static field initialiser can run before the scheme
    /// exists and fail with "invalid port specified" - which is exactly what happens in a test host
    /// that touches this type before creating an <see cref="Application"/>.
    /// </remarks>
    public static Uri UriFor(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        EnsurePackSchemeRegistered();
        return new Uri($"pack://application:,,,/{ResourceAssembly};component/{relativePath}", UriKind.Absolute);
    }

    private static void EnsurePackSchemeRegistered() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    /// <summary>Token overrides applied while Windows reports high contrast.</summary>
    public static Uri HighContrastOverlayUri => UriFor("Themes/Overlays/HighContrast.xaml");

    /// <summary>Token overrides applied while the user prefers reduced animation.</summary>
    public static Uri ReducedMotionOverlayUri => UriFor("Themes/Overlays/ReducedMotion.xaml");

    /// <summary>True when high-contrast token overrides should be active.</summary>
    public static bool IsHighContrastPreferred =>
        ReadOverride(HighContrastOverrideVariable) ?? SystemParameters.HighContrast;

    /// <summary>True when non-essential animation is permitted.</summary>
    public static bool IsMotionPreferred =>
        ReadOverride(ReducedMotionOverrideVariable) is { } reduced
            ? !reduced
            : SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Ensures the design system is loaded and keeps overlays in step with Windows settings.
    /// Safe to call more than once.
    /// </summary>
    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        EnsureThemeLoaded(application);
        AppScroll.EnableBoundaryAwareWheel();
        ApplyOverlays(application);
        if (subscribed)
            return;
        subscribed = true;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    /// <summary>
    /// Applies accessibility preferences, background-click focus clearing and shared chrome to a
    /// window. Every product window and dialog calls this once before it is shown, so input focus
    /// never behaves differently merely because a workflow opened a separate surface.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        AppMotion.SetIsEnabled(window, IsMotionPreferred);
        AppAccessibility.SetIsHighContrast(window, IsHighContrastPreferred);
        AppInput.TrackInputMode(window);
        AppFocus.ClearFocusOnBackgroundClick(window);
        AppWindowChrome.Apply(window);
    }

    /// <summary>
    /// Overrides the preferences for a single window. Used by the Design Gallery to preview
    /// high-contrast and Reduced-Motion states without changing Windows settings.
    /// </summary>
    public static void ApplyPreview(Window window, bool highContrast, bool motionEnabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        AppMotion.SetIsEnabled(window, motionEnabled);
        AppAccessibility.SetIsHighContrast(window, highContrast);
        SetOverlay(window.Resources, HighContrastOverlayUri, highContrast);
        SetOverlay(window.Resources, ReducedMotionOverlayUri, !motionEnabled);
    }

    /// <summary>
    /// Guarantees the whole design system is present, in order, without duplicating anything
    /// App.xaml already merged. Lets a test host or a tool build the real resource tree with one call.
    /// </summary>
    private static void EnsureThemeLoaded(Application application)
    {
        var insertAt = 0;
        foreach (var relativePath in ThemeDictionaries)
        {
            var uri = UriFor(relativePath);
            var existing = Find(application.Resources, uri);
            if (existing is not null)
            {
                insertAt = application.Resources.MergedDictionaries.IndexOf(existing) + 1;
                continue;
            }

            application.Resources.MergedDictionaries.Insert(insertAt, new ResourceDictionary { Source = uri });
            insertAt++;
        }
    }

    private static void ApplyOverlays(Application application)
    {
        SetOverlay(application.Resources, HighContrastOverlayUri, IsHighContrastPreferred);
        SetOverlay(application.Resources, ReducedMotionOverlayUri, !IsMotionPreferred);
    }

    private static void SetOverlay(ResourceDictionary target, Uri source, bool enabled)
    {
        var existing = Find(target, source);
        if (enabled)
        {
            if (existing is null)
                target.MergedDictionaries.Add(new ResourceDictionary { Source = source });
            return;
        }

        if (existing is not null)
            target.MergedDictionaries.Remove(existing);
    }

    private static ResourceDictionary? Find(ResourceDictionary target, Uri source) =>
        target.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source is not null &&
            string.Equals(dictionary.Source.ToString(), source.ToString(), StringComparison.OrdinalIgnoreCase));

    private static void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)))
            return;
        var application = Application.Current;
        if (application is null)
            return;
        application.Dispatcher.Invoke(() =>
        {
            ApplyOverlays(application);
            foreach (var window in application.Windows.OfType<Window>())
                Attach(window);
        });
    }

    private static bool? ReadOverride(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim() is "1" or "true" or "TRUE" or "True" or "yes";
    }
}
