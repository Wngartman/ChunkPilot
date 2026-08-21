using System.Xml.Linq;

namespace ChunkPilot.UnitTests.DesignSystem;

/// <summary>
/// Locates the files the design-system contract applies to.
/// </summary>
/// <remarks>
/// <para>
/// The governed set is defined by exclusion: every XAML file in the WPF project is governed
/// <em>except</em> the views that have not been rebuilt yet. A new page is therefore governed the
/// moment it is created, with nobody having to remember to add it to a list.
/// </para>
/// <para>
/// <see cref="LegacyViewsAwaitingRebuild"/> is the only escape hatch, and it is asserted to stay
/// exactly this size so it can only ever shrink.
/// </para>
/// </remarks>
internal static class DesignSystemFiles
{
    /// <summary>
    /// Views still carrying the previous design system. Each later phase deletes entries here; the
    /// list must never grow.
    /// </summary>
    public static IReadOnlyList<string> LegacyViewsAwaitingRebuild { get; } =
    [
        "ImportServerWindow.xaml"
    ];

    /// <summary>C# files permitted to raise a Windows message box, pending the dialog rebuild.</summary>
    public static IReadOnlyList<string> LegacyMessageBoxSurfaces { get; } =
    [
        "App.xaml.cs",
        "DialogService.cs",
        "ImportServerWindow.xaml.cs"
    ];

    /// <summary>The XAML namespace used for <c>x:Key</c>.</summary>
    public static XNamespace XamlNamespace { get; } = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static string RepositoryRoot { get; } = LocateRepositoryRoot();

    public static string AppProjectDirectory => Path.Combine(RepositoryRoot, "src", "ChunkPilot.App");

    public static string ThemesDirectory => Path.Combine(AppProjectDirectory, "Themes");

    public static string PaletteFile => Path.Combine(ThemesDirectory, "Tokens", "Palette.xaml");

    public static string ColorTokensFile => Path.Combine(ThemesDirectory, "Tokens", "ColorTokens.xaml");

    public static string MotionTokensFile => Path.Combine(ThemesDirectory, "Tokens", "MotionTokens.xaml");

    public static string HighContrastOverlayFile => Path.Combine(ThemesDirectory, "Overlays", "HighContrast.xaml");

    public static string ReducedMotionOverlayFile => Path.Combine(ThemesDirectory, "Overlays", "ReducedMotion.xaml");

    public static string CompatibilityFile => Path.Combine(ThemesDirectory, "Compatibility", "LegacyAliases.xaml");

    public static string AppXamlFile => Path.Combine(AppProjectDirectory, "App.xaml");

    public static string SplashWindowFile => Path.Combine(AppProjectDirectory, "SplashWindow.xaml");

    public static string ComponentCatalogFile => Path.Combine(RepositoryRoot, "docs", "architecture", "UI-COMPONENT-CATALOG.md");

    /// <summary>Every XAML file in the WPF project, excluding build output.</summary>
    public static IReadOnlyList<string> AllXaml() => Enumerate(AppProjectDirectory, "*.xaml");

    /// <summary>Every C# file in the WPF project, excluding build output and generated code.</summary>
    public static IReadOnlyList<string> AllCSharp() =>
        Enumerate(AppProjectDirectory, "*.cs")
            .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>XAML that must obey the design-system rules.</summary>
    public static IReadOnlyList<string> GovernedXaml() =>
        AllXaml()
            .Where(path => !LegacyViewsAwaitingRebuild.Contains(Path.GetFileName(path)))
            .ToArray();

    /// <summary>The shared control-style dictionaries that define the public component vocabulary.</summary>
    public static IReadOnlyList<string> ThemeControlXaml() =>
        Enumerate(Path.Combine(ThemesDirectory, "Controls"), "*.xaml");

    /// <summary>The Design Gallery XAML, where every demonstrated component must appear.</summary>
    public static IReadOnlyList<string> GalleryXaml() =>
        Enumerate(Path.Combine(AppProjectDirectory, "DesignSystem", "Gallery"), "*.xaml");

    /// <summary>Reads the top-level resource keys a dictionary declares.</summary>
    /// <remarks>
    /// Only direct children of the dictionary root count. Keys nested inside a template belong to
    /// that template's private scope and are not part of the public vocabulary.
    /// </remarks>
    public static IReadOnlyList<string> DeclaredKeys(string xamlFile)
    {
        var root = XDocument.Load(xamlFile).Root;
        if (root is null)
            return [];
        return root.Elements()
            .Select(element => element.Attribute(XamlNamespace + "Key")?.Value)
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .ToArray();
    }

    /// <summary>Reads the merged-dictionary sources declared by a XAML file, in order.</summary>
    public static IReadOnlyList<string> MergedDictionarySources(string xamlFile) =>
        XDocument.Load(xamlFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Select(element => element.Attribute("Source")?.Value)
            .Where(source => !string.IsNullOrEmpty(source))
            .Select(source => source!.Replace('\\', '/'))
            .ToArray();

    private static IReadOnlyList<string> Enumerate(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(DesignSystemFiles).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("ChunkPilot repository root not found.");
    }
}
