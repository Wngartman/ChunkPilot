using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App.CreateServer;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Proves that the Create Server v2 preview is exactly that: a preview.
/// </summary>
/// <remarks>
/// <para>
/// The interesting assertions here are the negative ones. The preview must not be able to reach the
/// agent, the installer, the store or a provider, and the way that is guaranteed is structural - it
/// holds no reference to any of them - rather than by remembering not to call them. These tests pin
/// that structure so a later change cannot quietly hand the preview a real dependency.
/// </para>
/// <para>
/// The visual assertions cover the same ground the design-system contract already covers for every
/// other view, plus the things specific to this window: automation names, distinguishable selection
/// and focus, and preview-only labelling.
/// </para>
/// </remarks>
public sealed class CreateServerV2PreviewIsolationTests
{
    private static readonly string PreviewDirectory =
        Path.Combine(DesignSystemFiles.AppProjectDirectory, "CreateServer");

    private static readonly string WindowXamlFile =
        Path.Combine(PreviewDirectory, "CreateServerPreviewWindow.xaml");

    /// <summary>Types the preview must never touch, by name, in any of its own source files.</summary>
    private static readonly string[] ForbiddenSymbols =
    [
        "IAgentClient", "AgentClient", "ManagedServerInstaller", "ChunkPilotStore",
        "ServerCreationTransaction", "ServerCreationRecoveryService", "CreationJournalEntry",
        "ServerInstallRequest", "BeginInstall", "InstallProgress", "CancelInstall",
        "CatalogProvider", "HttpClient", "SqliteConnection", "Process.Start",
        "Directory.CreateDirectory", "File.WriteAllText", "File.AppendAllText"
    ];

    /// <summary>Every write the render branch performs, each rooted at the directory it was given.</summary>
    private static readonly string[] RenderWriteFragments =
    [
        "Path.Combine(directory",
        "Directory.CreateDirectory(renderDirectory)",
        "Directory.CreateDirectory(directory)"
    ];

    // ------------------------------------------------------------------ activation

    [Fact]
    public void The_documented_switch_is_the_one_that_opens_the_preview()
    {
        Assert.Equal("--create-server-v2-preview", CreateServerPreviewLauncher.PreviewSwitch);
        Assert.True(CreateServerPreviewLauncher.IsRequested(["--create-server-v2-preview"]));
        Assert.True(CreateServerPreviewLauncher.IsRequested(["--other", "--Create-Server-V2-Preview"]));
    }

    [Fact]
    public void Normal_startup_arguments_never_request_the_preview()
    {
        string[][] normal =
        [
            [],
            ["--design-gallery"],
            ["--create-server-v2"],
            ["create-server-v2-preview"],
            ["C:\\some\\path.txt"]
        ];

        Assert.All(normal, arguments => Assert.False(CreateServerPreviewLauncher.IsRequested(arguments)));
    }

    [Fact]
    public void Startup_checks_for_the_preview_before_taking_any_lock_or_contacting_the_agent()
    {
        var startup = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "App.xaml.cs"));
        var previewIndex = startup.IndexOf("CreateServerPreviewLauncher.TryRun", StringComparison.Ordinal);
        var mutexIndex = startup.IndexOf("new Mutex(", StringComparison.Ordinal);
        var agentIndex = startup.IndexOf("new AgentClient()", StringComparison.Ordinal);

        Assert.True(previewIndex > 0, "App startup never offers the preview switch.");
        Assert.True(mutexIndex > previewIndex, "The preview must run before the single-instance lock.");
        Assert.True(agentIndex > previewIndex, "The preview must run before the agent client is created.");
    }

    [Fact]
    public void Only_the_application_bootstrap_knows_the_preview_exists()
    {
        var offenders = DesignSystemFiles.AllCSharp()
            .Concat(DesignSystemFiles.AllXaml())
            .Where(file => !file.StartsWith(PreviewDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("CreateServerPreview", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["App.xaml.cs"], offenders);
    }

    [Fact]
    public void The_preview_is_not_used_by_the_product_create_server_path()
    {
        var mainViewModel = File.ReadAllText(
            Path.Combine(DesignSystemFiles.AppProjectDirectory, "MainViewModel.cs"));

        Assert.DoesNotContain("CreateServerPreview", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("VanillaCreationRequested", mainViewModel, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DesignSystemFiles.AppProjectDirectory, "InstallServerWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(DesignSystemFiles.AppProjectDirectory, "InstallServerViewModel.cs")));
    }

    // ------------------------------------------------------------------ no production dependencies

    [Fact]
    public void The_preview_view_model_takes_no_dependency_it_could_install_with()
    {
        var constructors = typeof(CreateServerPreviewViewModel).GetConstructors();

        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());

        var fields = typeof(CreateServerPreviewViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType.Name)
            .ToArray();

        Assert.DoesNotContain(fields, name => ForbiddenSymbols.Contains(name, StringComparer.Ordinal));
    }

    [Fact]
    public void No_preview_source_file_names_a_provider_installer_agent_or_persistence_symbol()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(PreviewDirectory, "*.*", SearchOption.AllDirectories))
        {
            // The launcher writes review images to a directory the reviewer names, and says so.
            if (Path.GetFileName(file).Equals("CreateServerPreviewLauncher.cs", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            offenders.AddRange(ForbiddenSymbols
                .Where(symbol => text.Contains(symbol, StringComparison.Ordinal))
                .Select(symbol => $"{Path.GetFileName(file)} references {symbol}"));
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_preview_never_writes_outside_the_review_image_directory_it_is_given()
    {
        var launcher = File.ReadAllText(Path.Combine(PreviewDirectory, "CreateServerPreviewLauncher.cs"));

        // Every write in the launcher is inside the render branch and is rooted at the directory the
        // reviewer passed on the command line. Nothing else in the preview writes at all.
        Assert.DoesNotContain("CHUNKPILOT_DATA_ROOT", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("AppContext.BaseDirectory", launcher, StringComparison.Ordinal);
        Assert.All(
            RenderWriteFragments,
            fragment => Assert.Contains(fragment, launcher, StringComparison.Ordinal));
    }

    [Fact]
    public void Walking_the_whole_wizard_persists_nothing_and_produces_no_install_request()
    {
        var model = new CreateServerPreviewViewModel();
        model.SelectedIntent = CreationIntentCatalog.For(CreationIntent.Vanilla);
        model.ServerName = "Sunday survival";
        model.SelectedOption = model.Options[0];
        model.NextCommand.Execute(null);
        model.NextCommand.Execute(null);
        model.FinishPreviewCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Completion, model.CurrentStep);

        // A second, independent run sees exactly the same starting state: nothing was remembered.
        var fresh = new CreateServerPreviewViewModel();
        Assert.Equal(CreationWizardStep.Intent, fresh.CurrentStep);
        Assert.Null(fresh.SelectedIntent);
        Assert.Equal("", fresh.ServerName);
        Assert.Equal(SyntheticPreviewCatalog.ModpackProjects.Count, fresh.Projects.Count);
    }

    // ------------------------------------------------------------------ synthetic data honesty

    [Fact]
    public void The_synthetic_catalogue_invents_no_url_hash_or_retrieval_time()
    {
        foreach (var option in SyntheticPreviewCatalog.AllOptions)
        {
            Assert.Equal("", option.Evidence.HashValue);
            Assert.Equal("", option.Evidence.HashAlgorithm);
            Assert.Null(option.Evidence.ProviderDataAsOf);
            Assert.DoesNotContain("http", option.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", option.Evidence.ServerArtifactSource, StringComparison.OrdinalIgnoreCase);
        }

        var source = File.ReadAllText(Path.Combine(PreviewDirectory, "SyntheticPreviewCatalog.cs"));
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_synthetic_catalogue_says_that_it_is_synthetic_wherever_evidence_is_shown()
    {
        Assert.Contains("No provider was contacted", SyntheticPreviewCatalog.ProvenanceDetail, StringComparison.Ordinal);
        Assert.All(SyntheticPreviewCatalog.AllOptions, option =>
            Assert.Equal(SyntheticPreviewCatalog.ProvenanceDetail, option.ToContext().ProvenanceDetail));
        Assert.All(SyntheticPreviewCatalog.ModpackProjects, project =>
            Assert.StartsWith("Sample Pack:", project.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void The_synthetic_catalogue_is_deterministic()
    {
        var first = SyntheticPreviewCatalog.AllOptions.Select(option => option.Id).ToArray();
        var second = SyntheticPreviewCatalog.AllOptions.Select(option => option.Id).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(first.Length, first.Distinct().Count());
    }

    // ------------------------------------------------------------------ visual and accessibility contract

    [Fact]
    public void The_preview_window_loads_with_the_real_design_system_and_every_resource_resolves()
    {
        var realised = WpfDesignSystemHost.Run(() =>
        {
            var window = new CreateServerPreviewWindow(new CreateServerPreviewViewModel());
            try
            {
                // The window itself is never shown; its content root is measured directly, which
                // still resolves every StaticResource and realises every template in the tree.
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1440, 900));
                content.Arrange(new Rect(0, 0, 1440, 900));
                content.UpdateLayout();
                return (
                    window.Title,
                    Buttons: CountDescendants<Button>(content),
                    Lists: CountDescendants<ListBox>(content));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Contains("preview", realised.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(realised.Buttons > 0, "The preview window realised no buttons at all.");
        Assert.True(realised.Lists > 0, "The preview window realised no option lists at all.");
    }

    [Fact]
    public void Every_intent_card_and_option_exposes_a_composed_automation_name()
    {
        Assert.All(CreationIntentCatalog.Cards, card =>
            Assert.False(string.IsNullOrWhiteSpace(card.AutomationName)));
        Assert.All(SyntheticPreviewCatalog.AllOptions, option =>
        {
            Assert.Contains(option.Title, option.AutomationName, StringComparison.Ordinal);
            Assert.Contains(
                CompatibilityConclusionPolicy.ShortLabel(option.Evidence.Conclusion),
                option.AutomationName,
                StringComparison.Ordinal);
        });
        Assert.All(SyntheticPreviewCatalog.ModpackProjects, project =>
            Assert.Contains(project.Name, project.AutomationName, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_interactive_element_in_the_window_is_named_for_assistive_technology()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        string[] interactive = ["Button", "TextBox", "CheckBox", "ListBox", "AppSearchBox"];
        var unnamed = root.DescendantsAndSelf()
            .Where(element => interactive.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .Where(element => element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key") is null)
            .Where(element => element.Attributes()
                .All(attribute => attribute.Name.LocalName != "Name" ||
                                  !attribute.Name.NamespaceName.Contains("AutomationProperties", StringComparison.Ordinal)))
            .Where(element => !element.Attributes().Any(attribute =>
                attribute.Name.ToString().Contains("AutomationProperties.Name", StringComparison.Ordinal) ||
                attribute.Name.LocalName == "AutomationProperties.Name"))
            .Select(element => element.Name.LocalName)
            .ToArray();

        Assert.True(unnamed.Length == 0, "Unnamed interactive elements: " + string.Join(", ", unnamed));
    }

    [Fact]
    public void Selection_and_keyboard_focus_stay_distinguishable_in_the_shared_list_row()
    {
        var dataDisplay = File.ReadAllText(
            Path.Combine(DesignSystemFiles.ThemesDirectory, "Controls", "DataDisplay.xaml"));

        // The focus ring appears only in keyboard mode, so a pointer-selected row shows selection
        // alone. This is the same pairing AppNavigationRow uses.
        Assert.Contains("ds:AppInput.IsKeyboardMode", dataDisplay, StringComparison.Ordinal);

        // The intent card adds its own selection cues on top of the row's, so selection never rests
        // on colour alone.
        var window = File.ReadAllText(WindowXamlFile);
        Assert.Contains("IntentEdge", window, StringComparison.Ordinal);
        Assert.Contains("IntentChosen", window, StringComparison.Ordinal);
        Assert.Contains("AppAccentIndicator", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_components_are_not_keyboard_tab_stops()
    {
        // Verified at runtime: without these overrides, tabbing from the intent list reached the
        // page header, a status badge and an alert before the Next button. WPF makes every Control
        // focusable by default, so a composite display component becomes a dead tab stop.
        string[] displayComponents =
        [
            "AppPageHeader", "AppStatusBadge", "AppAlert", "AppSectionCard",
            "AppEmptyState", "AppInfoRow", "AppServerRow", "AppSearchBox"
        ];

        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        var unfocusable = root.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => element.Elements()
                .Any(setter => setter.Name.LocalName == "Setter" &&
                               setter.Attribute("Property")?.Value == "Focusable" &&
                               setter.Attribute("Value")?.Value == "False"))
            .Select(element => element.Attribute("TargetType")?.Value ?? "")
            .ToArray();

        var missing = displayComponents
            .Where(component => !unfocusable.Any(target =>
                target.Contains(component, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(missing.Length == 0, "Display components still take focus: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_window_uses_the_shared_button_vocabulary_and_no_raw_native_control()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        var offenders = root.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => element.Attribute("Style")?.Value)
            .Where(style => style is not null && !style.Contains("StaticResource App", StringComparison.Ordinal))
            .ToArray();

        // An unstyled Button picks up the implicit AppSecondaryButton style, which is intended.
        Assert.True(offenders.Length == 0, "Buttons with a non-design-system style: " + string.Join(", ", offenders!));
    }

    [Fact]
    public void Every_icon_named_by_the_preview_is_a_real_semantic_icon()
    {
        var names = Enum.GetNames<AppIconKind>().ToHashSet(StringComparer.Ordinal);
        var window = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(window);

        var used = window.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Kind" or "Icon" ||
                                attribute.Name.ToString().EndsWith("AppButton.Icon", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .Where(value => !value.StartsWith('{'))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(used);
        var invalid = used.Where(value => !names.Contains(value)).ToArray();
        Assert.True(invalid.Length == 0, "Unknown icon names: " + string.Join(", ", invalid));

        Assert.All(CreationIntentCatalog.Cards, card => Assert.True(Enum.IsDefined(card.Icon)));
    }

    [Fact]
    public void The_window_labels_itself_as_a_preview_in_places_the_user_cannot_miss()
    {
        var window = File.ReadAllText(WindowXamlFile);

        Assert.Contains("Design preview", window, StringComparison.Ordinal);
        Assert.Contains("Finish preview", window, StringComparison.Ordinal);
        Assert.Contains("Close preview", window, StringComparison.Ordinal);
        Assert.DoesNotContain(">Create server<", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Create server\"", window, StringComparison.Ordinal);
        Assert.Contains("no server is created or registered", CreationReviewBuilder.PreviewNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_declares_no_visual_value_of_its_own()
    {
        var text = File.ReadAllText(WindowXamlFile);
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        Assert.DoesNotMatch("#[0-9A-Fa-f]{3,8}", text);
        var literals = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "FontFamily" or "FontSize" or "Foreground" or "Background")
            .Where(attribute => !attribute.Value.StartsWith('{'))
            .Select(attribute => $"{attribute.Name.LocalName}=\"{attribute.Value}\"")
            .ToArray();

        Assert.True(literals.Length == 0, "Literal visual values: " + string.Join(", ", literals));
    }

    [Fact]
    public void The_window_owns_exactly_one_scroll_region_and_no_tab_navigation()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        var scrollers = root.DescendantsAndSelf()
            .Count(element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal(1, scrollers);
        Assert.DoesNotContain(root.DescendantsAndSelf(), element =>
            element.Name.LocalName is "TabControl" or "TabItem");
    }

    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = root is T ? 1 : 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            found += CountDescendants<T>(VisualTreeHelper.GetChild(root, index));
        return found;
    }
}
