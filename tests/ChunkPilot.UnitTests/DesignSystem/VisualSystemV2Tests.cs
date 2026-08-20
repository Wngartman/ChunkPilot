using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Contracts introduced by Visual System v2.
/// </summary>
/// <remarks>
/// <para>
/// These are semantic contracts, not pixel comparisons. They assert the things that were
/// actually wrong and that a future change could silently break again: a weight token that
/// resolves to a face Windows does not ship, a surface ramp that collapses back into
/// near-black, an accent asked to do two incompatible jobs, and an application icon that is
/// one artwork downscaled into every frame.
/// </para>
/// <para>
/// Contrast is asserted two different ways on purpose. Text uses the WCAG ratio, which is the
/// standard's own metric. Surfaces use CIE L*, because the WCAG formula's +0.05 term dominates
/// between two dark colours and reports ~1.0 however visible the boundary actually is - judging
/// dark surfaces by WCAG ratio is what made the previous audit's first diagnosis wrong.
/// </para>
/// </remarks>
public sealed class VisualSystemV2Tests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static Color ColorOf(string key) =>
        WpfDesignSystemHost.Run(() =>
        {
            var brush = Application.Current.TryFindResource(key) as SolidColorBrush;
            Assert.NotNull(brush);
            return brush!.Color;
        });

    private static double Channel(byte raw)
    {
        var c = raw / 255d;
        return c <= 0.03928d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
    }

    private static double Luminance(Color c) =>
        (0.2126d * Channel(c.R)) + (0.7152d * Channel(c.G)) + (0.0722d * Channel(c.B));

    /// <summary>WCAG 2.x contrast ratio. Correct for text; misleading between two dark surfaces.</summary>
    private static double Ratio(Color a, Color b)
    {
        var (hi, lo) = Luminance(a) >= Luminance(b)
            ? (Luminance(a), Luminance(b))
            : (Luminance(b), Luminance(a));
        return (hi + 0.05d) / (lo + 0.05d);
    }

    /// <summary>CIE L*. The metric that predicts whether two dark surfaces look different.</summary>
    private static double LStar(Color c)
    {
        var y = Luminance(c);
        return y > 0.008856d ? (116d * Math.Cbrt(y)) - 16d : 903.3d * y;
    }

    private static double DeltaL(string a, string b) => LStar(ColorOf(b)) - LStar(ColorOf(a));

    // ── typography ────────────────────────────────────────────────────────

    [Fact]
    public void No_token_requests_a_font_weight_Windows_does_not_ship()
    {
        // Windows ships Light 300, Semilight 350, Regular 400, SemiBold 600, Bold 700, Black 900
        // for the Segoe UI families. A request for Medium 500 resolves UP to SemiBold with no
        // error, which silently collapses the middle of the hierarchy.
        var available = WpfDesignSystemHost.Run(() =>
            new FontFamily("Segoe UI Variable Text, Segoe UI")
                .GetTypefaces()
                .Select(t => t.Weight.ToOpenTypeWeight())
                .Distinct()
                .ToHashSet());

        foreach (var key in new[] { "AppFontWeightRegular", "AppFontWeightSemilight", "AppFontWeightStrong" })
        {
            var weight = WpfDesignSystemHost.Run(() => Application.Current.TryFindResource(key));
            Assert.True(weight is FontWeight, $"{key} must resolve to a FontWeight.");
            var open = ((FontWeight)weight!).ToOpenTypeWeight();
            Assert.True(
                available.Contains(open),
                $"{key} asks for weight {open}, which no installed Segoe UI face provides. " +
                $"Available: {string.Join(", ", available.OrderBy(w => w))}.");
        }
    }

    [Fact]
    public void The_retired_Medium_weight_token_is_gone()
    {
        Assert.Null(WpfDesignSystemHost.Run(() => Application.Current.TryFindResource("AppFontWeightMedium")));

        var offenders = DesignSystemFiles.AllXaml()
            .Where(path => File.ReadAllText(path).Contains("AppFontWeightMedium", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.True(offenders.Length == 0, "AppFontWeightMedium still referenced by: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Chrome_text_is_not_as_heavy_as_a_section_heading()
    {
        // Buttons and field labels are chrome. When they carried Medium - and therefore rendered
        // SemiBold - every control shouted as loudly as a heading, which is most of why the type
        // read as blocky. Regular here is the fix, and it must stay Regular.
        foreach (var key in new[] { "AppLabelText", "AppSegmentedItem" })
        {
            var style = WpfDesignSystemHost.Run(() => Application.Current.TryFindResource(key) as Style);
            Assert.NotNull(style);
            var weight = style!.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.FontWeightProperty
                                  || s.Property == TextBlock.FontWeightProperty)?.Value;
            Assert.True(
                weight is null || (FontWeight)weight == FontWeights.Normal,
                $"{key} must use Regular; found {weight}.");
        }
    }

    // ── surfaces ──────────────────────────────────────────────────────────

    [Theory]
    // Perceptual separation, in L*. See the class remarks for why these are not WCAG ratios.
    [InlineData("AppSurfaceCanvas", "AppSurfaceRaised", 4.0)]   // a card must read as an object
    [InlineData("AppSurfaceRaised", "AppStroke", 12.0)]         // the edge does most of the work
    [InlineData("AppSurfaceRaised", "AppSurfaceHover", 3.5)]    // hover must be noticed
    [InlineData("AppSurfaceHover", "AppSurfacePressed", 2.5)]   // pressed must differ from hover
    [InlineData("AppSurfaceSunken", "AppSurfaceCanvas", 2.5)]   // the rail must separate
    public void Surface_boundaries_are_perceptibly_separated(string from, string to, double minimum)
    {
        var delta = Math.Abs(DeltaL(from, to));
        Assert.True(delta >= minimum,
            $"{from} -> {to} is only {delta:F1} L* apart; at least {minimum:F1} is required.");
    }

    [Fact]
    public void The_working_range_is_dark_but_not_black()
    {
        // The previous ramp ran L* 3.1-14.5, which read as black, left no headroom for a deeper
        // console well, and forced primary text to a harsh 17:1.
        var well = LStar(ColorOf("AppSurfaceWell"));
        var raised = LStar(ColorOf("AppSurfaceRaised"));
        Assert.True(well < 5.0, $"The well should be the one true black level; it is L* {well:F1}.");
        Assert.True(raised >= 15.0, $"A raised card should sit at L* 15 or above; it is {raised:F1}.");
        Assert.True(raised <= 24.0, $"A raised card should stay dark; it is L* {raised:F1}.");
    }

    // ── accent responsibilities ───────────────────────────────────────────

    [Fact]
    public void The_labelled_accent_fill_carries_its_text_at_AA()
    {
        var ratio = Ratio(ColorOf("AppTextOnAccent"), ColorOf("AppAccent"));
        Assert.True(ratio >= 4.5d,
            $"Text on the primary action measures {ratio:F2}:1; WCAG AA requires 4.5:1 at body size.");
    }

    [Fact]
    public void The_unlabelled_accent_indicator_clears_non_text_contrast()
    {
        // AppAccentIndicator marks selection and active edges with no text on it, so it is judged
        // against 1.4.11's 3:1 rather than against a text threshold. This is the whole reason the
        // accent is split: AppAccent cannot satisfy both this and the AA test above.
        foreach (var surface in new[] { "AppSurfaceRaised", "AppSurfaceCanvas", "AppSurfaceSunken" })
        {
            var ratio = Ratio(ColorOf("AppAccentIndicator"), ColorOf(surface));
            Assert.True(ratio >= 3.0d,
                $"AppAccentIndicator on {surface} measures {ratio:F2}:1; 3:1 is required.");
        }
    }

    [Fact]
    public void The_focus_ring_is_visible_on_an_accent_fill_and_on_a_card()
    {
        // Regression guard for a real defect: the ring measured 2.24:1 against the primary
        // button it was supposed to outline.
        var onAccent = Ratio(ColorOf("AppFocusRing"), ColorOf("AppAccent"));
        Assert.True(onAccent >= 3.0d, $"Focus ring on the accent fill measures {onAccent:F2}:1; 3:1 is required.");

        var onCard = Ratio(ColorOf("AppFocusRing"), ColorOf("AppSurfaceRaised"));
        Assert.True(onCard >= 3.0d, $"Focus ring on a raised card measures {onCard:F2}:1; 3:1 is required.");
    }

    [Fact]
    public void Danger_text_meets_AA_on_the_danger_fill()
    {
        // Regression guard: white on the previous danger fill measured 3.95:1.
        var ratio = Ratio(ColorOf("AppTextOnAccent"), ColorOf("AppDanger"));
        Assert.True(ratio >= 4.5d,
            $"Text on a danger button measures {ratio:F2}:1; WCAG AA requires 4.5:1.");
    }

    [Theory]
    [InlineData("AppTextPrimary", "AppSurfaceCanvas", 7.0)]
    [InlineData("AppTextPrimary", "AppSurfaceRaised", 7.0)]
    [InlineData("AppTextSecondary", "AppSurfaceRaised", 4.5)]
    [InlineData("AppTextMuted", "AppSurfaceRaised", 4.5)]
    [InlineData("AppTextAccent", "AppSurfaceRaised", 4.5)]
    public void Body_text_meets_AA_on_the_surfaces_it_is_used_on(string text, string surface, double minimum)
    {
        var ratio = Ratio(ColorOf(text), ColorOf(surface));
        Assert.True(ratio >= minimum, $"{text} on {surface} measures {ratio:F2}:1; {minimum:F1}:1 is required.");
    }

    [Fact]
    public void Primary_text_is_not_needlessly_harsh()
    {
        // Near-white on near-black is not a virtue. The old ramp produced 16.99:1, which reads as
        // stark rather than crisp; lifting the canvas brought it to a comfortable level.
        var ratio = Ratio(ColorOf("AppTextPrimary"), ColorOf("AppSurfaceCanvas"));
        Assert.True(ratio <= 15.5d, $"Primary text on the canvas measures {ratio:F2}:1, which is harsher than intended.");
    }

    // ── brand assets ──────────────────────────────────────────────────────

    private static string BrandDirectory => Path.Combine(DesignSystemFiles.RepositoryRoot, "assets", "brand");

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    public void Micro_icon_frames_fill_their_box(int size)
    {
        // The previous icon was one artwork downscaled into every frame: its bounding box was
        // 62.5% of the frame at every size and its ink covered 19%, which is why it looked about
        // half the size of neighbouring taskbar icons. These are the frames Windows actually
        // draws in the taskbar, title bar and Alt+Tab at 100/125/150% scaling.
        var (bboxPercent, inkPercent) = MeasureOccupancy(Path.Combine(BrandDirectory, $"ChunkPilot-{size}.png"), size);
        Assert.True(bboxPercent >= 94d, $"{size}px bounding box fills {bboxPercent:F1}% of the frame; 94% is required.");
        Assert.True(inkPercent >= 45d, $"{size}px ink covers {inkPercent:F1}% of the frame; 45% is required.");
    }

    [Fact]
    public void Every_icon_frame_is_drawn_at_its_own_size()
    {
        // A naive downscale produces an identical bounding-box percentage at every size. Real
        // per-size optical correction does not. This asserts the frames are genuinely different
        // drawings rather than one source resampled.
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var percentages = sizes
            .Select(s => MeasureOccupancy(Path.Combine(BrandDirectory, $"ChunkPilot-{s}.png"), s).BoundingBoxPercent)
            .ToArray();

        var spread = percentages.Max() - percentages.Min();
        Assert.True(spread >= 3d,
            "Every frame reports the same frame occupancy, which is the signature of one artwork " +
            "downscaled into every size rather than drawn per size. Spread: " + spread.ToString("F1", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_application_icon_carries_every_frame_Windows_asks_for()
    {
        var ico = Path.Combine(DesignSystemFiles.RepositoryRoot, "assets", "ChunkPilot.ico");
        Assert.True(File.Exists(ico), "assets/ChunkPilot.ico is missing.");

        using var stream = File.OpenRead(ico);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());            // reserved
        Assert.Equal(1, reader.ReadUInt16());            // type: icon
        var count = reader.ReadUInt16();

        var widths = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var w = reader.ReadByte();
            reader.ReadBytes(7);                          // height, palette, reserved, planes, bpp
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.True(length > 0, "An icon frame is empty.");
            Assert.True(offset + length <= stream.Length, "An icon frame points past the end of the file.");
            widths.Add(w == 0 ? 256 : w);
        }

        foreach (var required in new[] { 16, 24, 32, 48, 256 })
            Assert.Contains(required, widths);
    }

    [Fact]
    public void The_brand_source_is_reproducible()
    {
        Assert.True(
            File.Exists(Path.Combine(BrandDirectory, "build-brand-assets.ps1")),
            "The brand set must ship the script that regenerates it, so no frame is ever hand-edited " +
            "and then silently lost.");
    }

    private static (double BoundingBoxPercent, double InkPercent) MeasureOccupancy(string path, int expectedSize)
    {
        Assert.True(File.Exists(path), $"Missing brand asset: {path}");
        return WpfDesignSystemHost.Run(() =>
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(expectedSize, frame.PixelWidth);
            Assert.Equal(expectedSize, frame.PixelHeight);

            var stride = frame.PixelWidth * 4;
            var pixels = new byte[stride * frame.PixelHeight];
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);

            int minX = frame.PixelWidth, minY = frame.PixelHeight, maxX = -1, maxY = -1, opaque = 0;
            for (var y = 0; y < frame.PixelHeight; y++)
            {
                for (var x = 0; x < frame.PixelWidth; x++)
                {
                    if (pixels[(y * stride) + (x * 4) + 3] < 40)
                        continue;
                    opaque++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            Assert.True(maxX >= 0, $"{path} is fully transparent.");
            var box = Math.Max(maxX - minX + 1, maxY - minY + 1);
            return (100d * box / frame.PixelWidth,
                    100d * opaque / (frame.PixelWidth * (double)frame.PixelHeight));
        });
    }

    // ── core-page migration ──────────────────────────────────────────────

    [Theory]
    [InlineData("AppMeasureForm", 720d)]
    [InlineData("AppMeasureContent", 1280d)]
    public void Page_measure_tokens_resolve_to_the_documented_width(string key, double expected)
    {
        var value = WpfDesignSystemHost.Run(() => Application.Current.TryFindResource(key));
        Assert.Equal(expected, Assert.IsType<double>(value));
    }

    [Fact]
    public void The_shell_no_longer_drives_the_navigation_rail_width_from_code_behind()
    {
        // Regression guard for a real defect: a legacy SizeChanged handler set
        // NavRailColumn.Width directly using its own 1000/1200 thresholds, fighting the
        // AppNavigationRail style's ds:AppLayout.Mode trigger (900/1280) and reintroducing the
        // exact label-clipping the style trigger exists to prevent.
        var xaml = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "MainWindow.xaml"));
        Assert.DoesNotContain("SizeChanged=\"MainWindow_SizeChanged\"", xaml);

        var codeBehind = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "MainWindow.xaml.cs"));
        Assert.DoesNotContain("sidebarCollapsed", codeBehind);
        Assert.DoesNotContain("NavRailColumn.Width =", codeBehind);
    }

    [Fact]
    public void Automation_and_Activity_no_longer_render_a_bare_DataGrid()
    {
        // The previous Automation page was an empty administrative table in a large blank page;
        // Activity had the same shape. Both are now composed empty states plus responsive rows.
        var xaml = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "MainWindow.xaml"));
        Assert.DoesNotContain("<DataGrid", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_and_Servers_never_show_both_header_and_empty_state_create_actions_unconditionally()
    {
        // The previous Servers empty state showed Create/Add in the page header AND again in the
        // empty-state body, always. The header actions must now be conditioned on HasServers so
        // the two are never both visible at once.
        var path = Path.Combine(DesignSystemFiles.AppProjectDirectory, "MainWindow.xaml");
        var root = System.Xml.Linq.XDocument.Load(path).Root;
        Assert.NotNull(root);

        var ns = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var headerContentPresenters = root!.Descendants(ns + "AppPageHeader.PrimaryContent")
            .Concat(root.Descendants(ns + "AppPageHeader.SecondaryContent"));

        foreach (var slot in headerContentPresenters)
        {
            var button = slot.Descendants(ns + "Button").FirstOrDefault();
            if (button is null)
                continue;
            var content = button.Attribute("Content")?.Value ?? "";
            if (content is "Create server" or "Add existing")
            {
                var visibility = button.Attribute("Visibility")?.Value ?? "";
                Assert.True(
                    visibility.Contains("HasServers", StringComparison.Ordinal),
                    $"Header '{content}' action must be conditioned on HasServers so it is never " +
                    "shown alongside the same action already offered by the empty state.");
            }
        }
    }
}
