using System.Xml.Linq;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>Protects the normal-product routing cutover independently of the wizard's internal tests.</summary>
public sealed class CreateServerV2ProductCutoverTests
{
    private static readonly string AppDirectory = DesignSystemFiles.AppProjectDirectory;

    [Fact]
    public void Every_normal_create_server_cta_uses_the_same_vanilla_creation_command()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "MainWindow.xaml")).Root;
        Assert.NotNull(root);

        var createButtons = root.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => element.Attribute("Content")?.Value == "Create server")
            .ToArray();

        // Zero-server Dashboard, Servers header, and Servers empty state are three presentations of
        // one product action. None may drift back to the old installer independently.
        Assert.Equal(3, createButtons.Length);
        Assert.All(createButtons, button =>
            Assert.Equal("{Binding CreateVanillaServerCommand}", button.Attribute("Command")?.Value));
    }

    [Fact]
    public void Add_existing_remains_a_separate_by_reference_import_command()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "MainWindow.xaml")).Root;
        Assert.NotNull(root);

        var importButtons = root.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => element.Attribute("Content")?.Value.StartsWith("Add existing", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(3, importButtons.Length);
        Assert.All(importButtons, button =>
            Assert.Equal("{Binding AddServerCommand}", button.Attribute("Command")?.Value));

        var viewModel = File.ReadAllText(Path.Combine(AppDirectory, "MainViewModel.cs"));
        Assert.Contains("new ImportServerWindow(", viewModel, StringComparison.Ordinal);
        Assert.Contains("Server imported by reference", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void No_normal_product_route_constructs_the_superseded_installer()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppDirectory, "MainViewModel.cs"));
        var shellXaml = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));

        Assert.DoesNotContain("InstallServerCommand", shellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("new InstallServerWindow(", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new InstallServerViewModel(", viewModel, StringComparison.Ordinal);

        // The old broad implementation is deliberately retained until its advanced capabilities
        // have an explicit product home; it is dead to the normal Vanilla route, not deleted blindly.
        Assert.True(File.Exists(Path.Combine(AppDirectory, "InstallServerWindow.xaml")));
        Assert.True(File.Exists(Path.Combine(AppDirectory, "InstallServerViewModel.cs")));
    }

    [Fact]
    public void Shell_composes_the_live_gateway_completion_navigator_and_location_chooser_once()
    {
        var shell = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml.cs"));

        Assert.Contains("new AgentVanillaCreationGateway(client)", shell, StringComparison.Ordinal);
        Assert.Contains("new ShellCreatedServerNavigator(viewModel, this)", shell, StringComparison.Ordinal);
        Assert.Contains("new DialogServerLocationChooser(new DialogService())", shell, StringComparison.Ordinal);
        Assert.Contains("vanillaCreationWindow is { IsVisible: true }", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Retained_live_switch_enters_the_same_shell_product_route()
    {
        var startup = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

        Assert.Contains("CreateServerLiveLauncher.IsRequested(e.Args)", startup, StringComparison.Ordinal);
        Assert.Contains("((MainWindow)window).OpenVanillaCreation();", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateServerLiveLauncher.Open(\r\n                    window", startup, StringComparison.Ordinal);
    }
}
