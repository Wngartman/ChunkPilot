using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The long-standing UI resource contract, retargeted at the rebuilt design system.
/// </summary>
/// <remarks>
/// These are the coarse guarantees: the icon vocabulary exists, the theme loads in the documented
/// order, the semantic tokens and shared components are present, navigation uses stable identifiers,
/// and the icon package stays pinned. The fine-grained rules live in
/// <see cref="DesignSystemContractTests"/>.
/// </remarks>
public sealed class UIResourceContractTests
{
    [Fact]
    public void Semantic_icon_vocabulary_is_nonempty_and_has_core_actions()
    {
        var names = Enum.GetNames<AppIconKind>();

        Assert.Contains(nameof(AppIconKind.Home), names);
        Assert.Contains(nameof(AppIconKind.Server), names);
        Assert.Contains(nameof(AppIconKind.Settings), names);
        Assert.Contains(nameof(AppIconKind.Warning), names);
        Assert.Contains(nameof(AppIconKind.Play), names);
        Assert.Contains(nameof(AppIconKind.Stop), names);
        Assert.True(names.Length >= 30);
    }

    [Fact]
    public void App_resources_merge_tokens_before_components()
    {
        var sources = DesignSystemFiles.MergedDictionarySources(DesignSystemFiles.AppXamlFile).ToList();

        var lastToken = sources.FindLastIndex(source => source.Contains("/Tokens/", StringComparison.Ordinal));
        var firstControl = sources.FindIndex(source => source.Contains("/Controls/", StringComparison.Ordinal));
        var compatibility = sources.FindIndex(source => source.Contains("/Compatibility/", StringComparison.Ordinal));

        Assert.True(lastToken >= 0, "No token dictionaries are merged.");
        Assert.True(firstControl > lastToken, "Component dictionaries must be merged after the tokens they consume.");
        Assert.True(compatibility > firstControl, "The compatibility layer must be merged last.");
    }

    [Fact]
    public void Design_tokens_define_semantic_surface_and_accessibility_values()
    {
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppSurface"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppTextPrimary"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppAccent"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppFocusRing"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppInfo"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppDurationStandard"));
    }

    [Fact]
    public void Shared_component_resources_include_accessible_state_contracts()
    {
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppPageSurface"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppPrimaryButton"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppStatusBadge"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppEmptyState"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppLoadingState"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppAlert"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppScreenReaderText"));
    }

    [Fact]
    public void Navigation_items_use_stable_destination_ids_and_semantic_icons()
    {
        var viewModel = new MainViewModel(null!, null!);

        Assert.Equal("Dashboard", viewModel.NavigationItems[0].Page);
        Assert.Contains(viewModel.NavigationItems, item => item.Page == "Settings" && item.Icon == AppIconKind.Settings);
        Assert.DoesNotContain(viewModel.NavigationItems, item => string.IsNullOrWhiteSpace(item.Description));
    }

    [Fact]
    public void Approved_icon_dependency_is_centrally_pinned()
    {
        var packages = File.ReadAllText(Path.Combine(DesignSystemFiles.RepositoryRoot, "Directory.Packages.props"));
        var app = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "ChunkPilot.App.csproj"));

        Assert.Contains("FluentIcons.Wpf\" Version=\"2.1.333", packages, StringComparison.Ordinal);
        Assert.Contains("PackageReference Include=\"FluentIcons.Wpf\"", app, StringComparison.Ordinal);
    }
}
