using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Xml.Linq;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Enforces the design-system contract described in <c>docs/architecture/UI-DESIGN-SYSTEM.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// These tests are deliberately structural rather than pixel-based. They check the things that
/// actually rot: a resource that stops resolving, a page that invents a colour, a component that
/// exists in code but not in the catalogue or the gallery, an overlay that misses a new token.
/// </para>
/// <para>
/// The governed file set is defined by exclusion, so a page added next month is covered without
/// anyone updating a list. The one escape hatch, the views still awaiting rebuild, is pinned to its
/// current size and can only shrink.
/// </para>
/// </remarks>
public sealed class DesignSystemContractTests
{
    /// <summary>The compatibility layer's size today. This number must only ever go down.</summary>
    private const int CompatibilityAliasCeiling = 41;

    private static readonly string[] IconXamlSurface = ["Icons.xaml"];
    private static readonly string[] IconCodeSurface = ["AppIconConverters.cs", "AppIconMap.cs"];
    private static readonly string[] GalleryEntryPoint = ["App.xaml.cs"];

    private static readonly Regex HexColour = new(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled);
    private static readonly Regex PrivateUseGlyph = new(@"[\uE000-\uF8FF]", RegexOptions.Compiled);
    private static readonly Regex NonBasicMultilingualCharacter = new(@"[\uD800-\uDBFF]", RegexOptions.Compiled);

    // ---------------------------------------------------------------- loading

    [Fact]
    public void Every_resource_in_the_loaded_theme_resolves()
    {
        var resources = WpfDesignSystemHost.EnumerateResources();

        Assert.NotEmpty(resources);
        var unresolved = resources.Where(entry => entry.Value is null).ToArray();
        Assert.True(
            unresolved.Length == 0,
            "Resources failed to resolve: " +
            string.Join(", ", unresolved.Select(entry => $"{entry.Key} in {entry.Source}")));
    }

    [Fact]
    public void App_xaml_merges_exactly_the_dictionaries_AppTheme_declares()
    {
        var declared = DesignSystemFiles.MergedDictionarySources(DesignSystemFiles.AppXamlFile);

        Assert.Equal(AppTheme.ThemeDictionaries, declared);
    }

    [Fact]
    public void Theme_dictionaries_are_merged_flat_so_cross_file_references_resolve()
    {
        // Nesting these files behind a parent dictionary breaks deferred StaticResource and
        // Style BasedOn lookups. Each theme file may only merge value-only token dictionaries.
        foreach (var relativePath in AppTheme.ThemeDictionaries)
        {
            var file = Path.Combine(DesignSystemFiles.AppProjectDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var nested = DesignSystemFiles.MergedDictionarySources(file);
            Assert.All(nested, source =>
                Assert.True(
                    source.EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase),
                    $"{relativePath} merges {source}; only the value-only palette may be merged into a theme file."));
        }
    }

    [Fact]
    public void Required_token_roles_exist_with_the_expected_types()
    {
        AssertResourceType<SolidColorBrush>("AppSurfaceCanvas");
        AssertResourceType<SolidColorBrush>("AppSurface");
        AssertResourceType<SolidColorBrush>("AppSurfaceRaised");
        AssertResourceType<SolidColorBrush>("AppSurfaceSunken");
        AssertResourceType<SolidColorBrush>("AppSurfaceSelected");
        AssertResourceType<SolidColorBrush>("AppStroke");
        AssertResourceType<SolidColorBrush>("AppTextPrimary");
        AssertResourceType<SolidColorBrush>("AppTextSecondary");
        AssertResourceType<SolidColorBrush>("AppTextMuted");
        AssertResourceType<SolidColorBrush>("AppAccent");
        AssertResourceType<SolidColorBrush>("AppFocusRing");
        AssertResourceType<SolidColorBrush>("AppSuccess");
        AssertResourceType<SolidColorBrush>("AppWarning");
        AssertResourceType<SolidColorBrush>("AppDanger");
        AssertResourceType<SolidColorBrush>("AppInfo");
        AssertResourceType<SolidColorBrush>("AppNeutral");

        AssertResourceType<FontFamily>("AppFontFamily");
        AssertResourceType<FontFamily>("AppFontFamilyMono");
        AssertResourceType<double>("AppFontSizeBody");
        AssertResourceType<double>("AppLineHeightBody");

        // Typed, not coerced from a double: the previous system stored radii and padding as doubles
        // and relied on XAML type conversion at every use site.
        AssertResourceType<CornerRadius>("AppCornerCard");
        AssertResourceType<Thickness>("AppCardPadding");
        AssertResourceType<Thickness>("AppFocusRingThickness");
        AssertResourceType<double>("AppControlHeight");
        AssertResourceType<double>("AppBreakpointStandard");
        AssertResourceType<double>("AppBreakpointWide");

        AssertResourceType<DropShadowEffect>("AppElevationOverlay");
        AssertResourceType<Duration>("AppDurationStandard");
        AssertResourceType<Style>("AppPrimaryButton");
    }

    // ---------------------------------------------------------------- overlays

    [Fact]
    public void High_contrast_overlay_overrides_every_colour_token()
    {
        var tokens = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.ColorTokensFile);
        var overridden = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.HighContrastOverlayFile);

        Assert.NotEmpty(tokens);
        var missing = tokens.Except(overridden, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "High-contrast overlay does not override: " + string.Join(", ", missing));
    }

    [Fact]
    public void Reduced_motion_overlay_overrides_every_motion_token()
    {
        var tokens = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.MotionTokensFile);
        var overridden = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.ReducedMotionOverlayFile);

        Assert.NotEmpty(tokens);
        var missing = tokens.Except(overridden, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "Reduced-motion overlay does not override: " + string.Join(", ", missing));
    }

    [Fact]
    public void Accessibility_overlays_load_and_zero_every_duration()
    {
        var highContrast = WpfDesignSystemHost.LoadDictionaryKeys(AppTheme.HighContrastOverlayUri);
        Assert.NotEmpty(highContrast);

        var durations = WpfDesignSystemHost.Run(() =>
        {
            var dictionary = new ResourceDictionary { Source = AppTheme.ReducedMotionOverlayUri };
            return dictionary.Keys.OfType<object>()
                .Select(key => dictionary[key])
                .OfType<Duration>()
                .ToArray();
        });

        Assert.NotEmpty(durations);
        Assert.All(durations, duration => Assert.Equal(TimeSpan.Zero, duration.TimeSpan));
    }

    // ---------------------------------------------------------------- keys and catalogue

    [Fact]
    public void No_resource_key_is_declared_in_two_theme_files()
    {
        var declarations = AppTheme.ThemeDictionaries
            .Select(relativePath => Path.Combine(
                DesignSystemFiles.AppProjectDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(file => DesignSystemFiles.DeclaredKeys(file).Select(key => (Key: key, File: Path.GetFileName(file))))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} in {string.Join(" and ", group.Select(entry => entry.File))}")
            .ToArray();

        Assert.True(declarations.Length == 0, "Duplicate resource keys: " + string.Join("; ", declarations));
    }

    [Fact]
    public void Public_component_keys_use_the_App_prefix_and_internals_use_Internal()
    {
        var stray = DesignSystemFiles.ThemeControlXaml()
            .SelectMany(file => DesignSystemFiles.DeclaredKeys(file).Select(key => (Key: key, File: Path.GetFileName(file))))
            .Where(entry => !entry.Key.StartsWith("App", StringComparison.Ordinal))
            .Where(entry => !entry.Key.StartsWith("Internal", StringComparison.Ordinal))
            .Select(entry => $"{entry.Key} in {entry.File}")
            .ToArray();

        Assert.True(stray.Length == 0, "Keys must be prefixed App or Internal: " + string.Join(", ", stray));
    }

    [Fact]
    public void Component_catalog_and_theme_agree_in_both_directions()
    {
        var catalog = ReadCatalog();
        var themeKeys = DesignSystemFiles.ThemeControlXaml()
            .SelectMany(DesignSystemFiles.DeclaredKeys)
            .Where(key => key.StartsWith("App", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(catalog);

        var undocumented = themeKeys.Except(catalog.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.True(
            undocumented.Length == 0,
            "Shared components missing from docs/architecture/UI-COMPONENT-CATALOG.md: " + string.Join(", ", undocumented));

        var phantom = catalog.Keys.Except(themeKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.True(
            phantom.Length == 0,
            "Catalogued components that do not exist in the theme: " + string.Join(", ", phantom));
    }

    [Fact]
    public void Every_catalogued_component_resolves_in_the_loaded_theme()
    {
        var unresolved = ReadCatalog().Keys
            .Where(key => WpfDesignSystemHost.Resolve(key) is null)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unresolved.Length == 0, "Catalogued keys that do not resolve: " + string.Join(", ", unresolved));
    }

    [Fact]
    public void Design_gallery_shows_every_component_marked_shown()
    {
        var gallery = string.Concat(DesignSystemFiles.GalleryXaml().Select(File.ReadAllText));

        var missing = ReadCatalog()
            .Where(entry => entry.Value == "shown")
            .Select(entry => entry.Key)
            .Where(key => !gallery.Contains(key, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Components marked 'shown' but absent from the Design Gallery: " + string.Join(", ", missing));
    }

    [Fact]
    public void Design_gallery_demonstrates_every_composite_component()
    {
        var gallery = string.Concat(DesignSystemFiles.GalleryXaml().Select(File.ReadAllText));
        var componentTypes = Directory
            .EnumerateFiles(Path.Combine(DesignSystemFiles.AppProjectDirectory, "DesignSystem", "Components"), "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        Assert.NotEmpty(componentTypes);
        var missing = componentTypes
            .Where(name => !gallery.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(missing.Length == 0, "Components absent from the Design Gallery: " + string.Join(", ", missing));
    }

    // ---------------------------------------------------------------- icons

    [Fact]
    public void Every_icon_intent_maps_to_a_distinct_glyph()
    {
        var kinds = Enum.GetValues<AppIconKind>();
        var mapped = kinds.ToDictionary(kind => kind, AppIconMap.Resolve);

        Assert.Equal(kinds.Length, mapped.Count);
        var shared = mapped
            .GroupBy(entry => entry.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} used by {string.Join(" and ", group.Select(entry => entry.Key))}")
            .ToArray();
        Assert.True(shared.Length == 0, "Two intents map to the same glyph: " + string.Join("; ", shared));
    }

    [Fact]
    public void Icon_scales_measure_at_their_documented_sizes()
    {
        // The gallery's icon-scale row rendered four identical glyphs. The size step reached the
        // icon package, which uses it to pick a purpose-drawn glyph, but nothing sized the element,
        // so every icon in the application rendered at the package default of 20 dip.
        var measured = WpfDesignSystemHost.Run(() => Enum.GetValues<AppIconScale>()
            .Select(scale =>
            {
                var icon = new AppIcon { Kind = AppIconKind.Server, Scale = scale };
                var host = new Border { Child = icon };
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                return (Scale: scale, icon.DesiredSize);
            })
            .ToArray());

        (AppIconScale Scale, double Size)[] expected =
        [
            (AppIconScale.Small, 16d),
            (AppIconScale.Medium, 20d),
            (AppIconScale.Large, 24d),
            (AppIconScale.Hero, 32d)
        ];

        var wrong = expected
            .Select(entry => (entry.Scale, entry.Size, Actual: measured.Single(item => item.Scale == entry.Scale).DesiredSize))
            .Where(entry => entry.Actual.Width != entry.Size || entry.Actual.Height != entry.Size)
            .Select(entry => $"{entry.Scale} expected {entry.Size} but measured {entry.Actual.Width}x{entry.Actual.Height}")
            .ToArray();

        Assert.True(wrong.Length == 0, "Icon size steps are not applied: " + string.Join("; ", wrong));
    }

    [Fact]
    public void A_label_that_follows_a_control_glyph_keeps_a_leading_gap()
    {
        // AppInlineGap is a trailing gap. Applying it to the element that follows leaves the label
        // touching the check box, radio button, switch or navigation icon, which is exactly what the
        // first gallery capture showed.
        var offenders = WpfDesignSystemHost.Run(() => new[]
            {
                MeasureLabelGap("AppCheckBox", new CheckBox { Content = "Create a recovery point first" }),
                MeasureLabelGap("AppRadioButton", new RadioButton { Content = "Reference the folder in place" }),
                MeasureLabelGap("AppToggleSwitch", new CheckBox
                {
                    Content = "Automatic backups",
                    Style = (Style)Application.Current.FindResource("AppToggleSwitch")
                })
            }
            .Where(entry => entry.Gap <= 0d)
            .Select(entry => $"{entry.Component} leaves a gap of {entry.Gap}")
            .ToArray());

        Assert.True(offenders.Length == 0, "Labels must not touch their control glyph: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Icon_package_is_referenced_from_exactly_one_xaml_file()
    {
        var referencing = DesignSystemFiles.GovernedXaml()
            .Where(file => File.ReadAllText(file).Contains("FluentIcons", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToArray();

        Assert.Equal(IconXamlSurface, referencing);
    }

    [Fact]
    public void Icon_package_is_referenced_from_exactly_one_code_file()
    {
        var referencing = DesignSystemFiles.AllCSharp()
            .Where(file => File.ReadAllText(file).Contains("FluentIcons", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(IconCodeSurface, referencing);
    }

    // ---------------------------------------------------------------- governed XAML rules

    [Fact]
    public void Only_the_palette_declares_literal_colours()
    {
        var offenders = GovernedExcept(DesignSystemFiles.PaletteFile)
            .Where(file => HexColour.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0, "Literal colours outside Palette.xaml: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Code_does_not_construct_colours_by_hand()
    {
        var offenders = DesignSystemFiles.AllCSharp()
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("Color.FromRgb", StringComparison.Ordinal) ||
                       text.Contains("Color.FromArgb", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0, "Colours constructed in code instead of resolved from tokens: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Governed_xaml_does_not_declare_typography_values()
    {
        var offenders = new List<string>();
        foreach (var file in DesignSystemFiles.GovernedXaml())
        {
            var root = XDocument.Load(file).Root;
            if (root is null)
                continue;

            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.LocalName is "FontFamily" or "FontSize" && !attribute.Value.StartsWith('{'))
                        offenders.Add($"{Path.GetFileName(file)}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                }

                if (element.Name.LocalName != "Setter")
                    continue;
                var property = element.Attribute("Property")?.Value;
                var value = element.Attribute("Value")?.Value;
                if (property is "FontFamily" or "FontSize" && value is not null && !value.StartsWith('{'))
                    offenders.Add($"{Path.GetFileName(file)}: Setter {property}=\"{value}\"");
            }
        }

        Assert.True(offenders.Count == 0, "Typography must come from tokens: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Governed_xaml_contains_no_glyph_literals_or_icon_fonts()
    {
        var offenders = new List<string>();
        foreach (var file in DesignSystemFiles.GovernedXaml())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (text.Contains("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{name}: Segoe Fluent Icons");
            if (text.Contains("Segoe MDL2", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{name}: Segoe MDL2");
            if (PrivateUseGlyph.IsMatch(text))
                offenders.Add($"{name}: private-use glyph literal");
            if (NonBasicMultilingualCharacter.IsMatch(text))
                offenders.Add($"{name}: emoji or other non-BMP character");
        }

        Assert.True(offenders.Count == 0, "Icon contract violations: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Governed_xaml_does_not_use_tab_navigation()
    {
        // Parsed rather than text-matched, so the documentation comments that explain why tabs are
        // banned do not count as violations.
        var offenders = new List<string>();
        foreach (var file in DesignSystemFiles.GovernedXaml())
        {
            var root = XDocument.Load(file).Root;
            if (root is null)
                continue;
            var uses = root.DescendantsAndSelf().Any(element =>
                element.Name.LocalName is "TabControl" or "TabItem" ||
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName is "TargetType" or "BasedOn" or "Style" &&
                    (attribute.Value.Contains("TabControl", StringComparison.Ordinal) ||
                     attribute.Value.Contains("TabItem", StringComparison.Ordinal))));
            if (uses)
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0, "Use shell destinations or AppSegmentedControl instead of tabs: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Governed_xaml_never_nests_a_scroll_region_inside_another()
    {
        var offenders = new List<string>();
        foreach (var file in DesignSystemFiles.GovernedXaml())
        {
            var root = XDocument.Load(file).Root;
            if (root is null)
                continue;
            foreach (var viewer in root.DescendantsAndSelf().Where(element => element.Name.LocalName == "ScrollViewer"))
            {
                if (viewer.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ScrollViewer"))
                    offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0, "Nested scroll regions trap the wheel and hide content: " + string.Join(", ", offenders.Distinct()));
    }

    // ---------------------------------------------------------------- boundaries

    [Fact]
    public void Views_awaiting_rebuild_are_exactly_the_documented_set()
    {
        // Pinned so the escape hatch cannot grow. Each later phase deletes an entry.
        Assert.Single(DesignSystemFiles.LegacyViewsAwaitingRebuild);
        Assert.All(DesignSystemFiles.LegacyViewsAwaitingRebuild, name =>
            Assert.True(
                File.Exists(Path.Combine(DesignSystemFiles.AppProjectDirectory, name)),
                $"{name} is listed as awaiting rebuild but does not exist; remove it from the list."));
    }

    [Fact]
    public void Compatibility_layer_only_shrinks()
    {
        var aliases = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.CompatibilityFile);

        Assert.True(
            aliases.Count <= CompatibilityAliasCeiling,
            $"LegacyAliases.xaml has {aliases.Count} aliases but the ceiling is {CompatibilityAliasCeiling}. " +
            "The compatibility layer must only ever shrink.");
    }

    [Fact]
    public void Rebuilt_views_do_not_use_the_compatibility_layer()
    {
        var aliases = DesignSystemFiles.DeclaredKeys(DesignSystemFiles.CompatibilityFile)
            .Where(key => !key.StartsWith("App", StringComparison.Ordinal))
            .ToArray();

        var offenders = new List<string>();
        foreach (var file in DesignSystemFiles.GovernedXaml())
        {
            if (string.Equals(Path.GetFileName(file), "LegacyAliases.xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = File.ReadAllText(file);
            foreach (var alias in aliases)
            {
                if (text.Contains($"StaticResource {alias}}}", StringComparison.Ordinal) ||
                    text.Contains($"DynamicResource {alias}}}", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} uses {alias}");
            }
        }

        Assert.True(offenders.Count == 0, "Rebuilt views must use the new tokens: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Message_boxes_are_confined_to_the_documented_legacy_surfaces()
    {
        var surfaces = DesignSystemFiles.AllCSharp()
            .Where(file => File.ReadAllText(file).Contains("MessageBox.Show", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            DesignSystemFiles.LegacyMessageBoxSurfaces.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            surfaces);
    }

    [Fact]
    public void Design_gallery_is_not_reachable_from_the_product_interface()
    {
        var galleryDirectory = Path.Combine(DesignSystemFiles.AppProjectDirectory, "DesignSystem", "Gallery");
        var offenders = DesignSystemFiles.AllCSharp()
            .Concat(DesignSystemFiles.AllXaml())
            .Where(file => !file.StartsWith(galleryDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("DesignGallery", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Only the application bootstrap may know the gallery exists, and only via a command-line switch.
        Assert.Equal(GalleryEntryPoint, offenders);
    }

    // ---------------------------------------------------------------- responsive

    [Fact]
    public void Layout_modes_follow_the_documented_breakpoints()
    {
        var modes = WpfDesignSystemHost.Run(() => new[]
        {
            AppLayout.ModeForWidth(640),
            AppLayout.ModeForWidth(899),
            AppLayout.ModeForWidth(900),
            AppLayout.ModeForWidth(1279),
            AppLayout.ModeForWidth(1280),
            AppLayout.ModeForWidth(1920)
        });

        Assert.Equal(
            [
                AppLayoutMode.Compact,
                AppLayoutMode.Compact,
                AppLayoutMode.Standard,
                AppLayoutMode.Standard,
                AppLayoutMode.Wide,
                AppLayoutMode.Wide
            ],
            modes);
    }

    // ---------------------------------------------------------------- text hierarchy

    [Fact]
    public void Every_text_foreground_token_exists_and_resolves()
    {
        var required = new[]
        {
            "AppTextPrimary",
            "AppTextSecondary",
            "AppTextTertiary",
            "AppTextMuted",
            "AppTextDisabled",
            "AppTextOnAccent",
            "AppTextAccent"
        };

        var missing = new List<string>();
        foreach (var token in required)
        {
            var value = WpfDesignSystemHost.Resolve(token);
            if (value is null)
                missing.Add(token);
            else if (value is not SolidColorBrush)
                missing.Add($"{token} (expected SolidColorBrush, got {value.GetType().Name})");
        }

        Assert.True(
            missing.Count == 0,
            "Text foreground tokens missing or wrong type: " + string.Join("; ", missing));
    }

    [Fact]
    public void SplashWindow_does_not_reference_status_text()
    {
        var xaml = File.ReadAllText(DesignSystemFiles.SplashWindowFile);
        Assert.False(
            xaml.Contains("StatusText", StringComparison.Ordinal),
            "SplashWindow.xaml must not contain status text elements; the startup splash is icon-only.");
    }

    [Fact]
    public void SplashWindow_uses_a_large_enough_window()
    {
        var xaml = XDocument.Load(DesignSystemFiles.SplashWindowFile).Root;
        Assert.NotNull(xaml);

        var width = xaml.Attribute("Width")?.Value;
        var height = xaml.Attribute("Height")?.Value;

        Assert.NotNull(width);
        Assert.NotNull(height);

        double w = double.Parse(width!);
        double h = double.Parse(height!);

        Assert.True(
            w >= 200 && h >= 200,
            $"Splash window {w}x{h} is too small; minimum 200x200 is required for icon clarity.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Realises a control's template and reports the horizontal gap its label starts with.
    /// </summary>
    private static (string Component, double Gap) MeasureLabelGap(string component, Control control)
    {
        var host = new Border { Child = control };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        var presenter = FindDescendant<ContentPresenter>(control);
        return (component, presenter?.Margin.Left ?? 0d);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static IEnumerable<string> GovernedExcept(string excluded) =>
        DesignSystemFiles.GovernedXaml()
            .Where(file => !string.Equals(file, excluded, StringComparison.OrdinalIgnoreCase));

    private static void AssertResourceType<T>(string key)
    {
        var value = WpfDesignSystemHost.Resolve(key);
        Assert.NotNull(value);
        Assert.IsAssignableFrom<T>(value);
    }

    /// <summary>
    /// Reads the catalogue table: key to gallery marker.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadCatalog()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(DesignSystemFiles.ComponentCatalogFile))
        {
            if (!line.StartsWith('|'))
                continue;
            var cells = line.Split('|').Select(cell => cell.Trim().Trim('`').Trim()).ToArray();
            if (cells.Length < 7)
                continue;
            var key = cells[1];
            if (!Regex.IsMatch(key, @"^App[A-Za-z0-9]+$"))
                continue;
            rows[key] = cells[5];
        }
        return rows;
    }
}
