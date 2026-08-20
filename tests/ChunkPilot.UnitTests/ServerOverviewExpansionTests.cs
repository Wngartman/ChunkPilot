using System.Text;
using System.Text.Json;
using System.Windows;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem.Components;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

public sealed class ServerOverviewExpansionTests
{
    [Theory]
    [InlineData(WindowState.Minimized, false, false)]
    [InlineData(WindowState.Minimized, true, true)]
    [InlineData(WindowState.Normal, true, false)]
    public void Tray_hiding_respects_the_saved_preference(
        WindowState state, bool trayEnabled, bool expected) =>
        Assert.Equal(expected, ChunkPilot.App.App.ShouldHideToTray(state, trayEnabled));

    [Fact]
    public void Official_skin_profile_parser_accepts_only_the_minecraft_texture_host()
    {
        var accepted = ProfileWithTexture("http://textures.minecraft.net/texture/abc123");
        Assert.True(MinecraftSkinProfileParser.TryGetTextureUri(accepted, out var uri));
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("textures.minecraft.net", uri.Host);

        var rejected = ProfileWithTexture("https://example.invalid/player.png");
        Assert.False(MinecraftSkinProfileParser.TryGetTextureUri(rejected, out _));
    }

    [Theory]
    [InlineData("keepInventory", "true")]
    [InlineData("minecraft:playersSleepingPercentage", "75")]
    [InlineData("futureRule_26", "enabled")]
    public void Custom_gamerule_path_is_version_independent_and_command_safe(string name, string value) =>
        Assert.Null(GamerulePolicy.ValidateCustom(name, value));

    [Theory]
    [InlineData("say hacked", "true")]
    [InlineData("keepInventory", "true false")]
    [InlineData("", "true")]
    public void Custom_gamerule_path_rejects_command_injection(string name, string value) =>
        Assert.NotNull(GamerulePolicy.ValidateCustom(name, value));

    [Fact]
    public void Generic_query_parser_can_verify_a_rule_not_in_the_curated_table()
    {
        var parsed = GamerulePolicy.ParseReportedValueAny(
            "Gamerule futureRule_26 is currently set to: enabled");
        Assert.Equal("futureRule_26", parsed!.Value.Name);
        Assert.Equal("enabled", parsed.Value.Value);
        Assert.Null(GamerulePolicy.ParseReportedValue(
            "Gamerule futureRule_26 is currently set to: enabled"));
        Assert.Null(GamerulePolicy.ParseReportedValueAny(
            "[Server thread/INFO]: <Someone> Gamerule futureRule_26 is currently set to: disabled"));
    }

    [Fact]
    public void Overview_and_shared_controls_carry_the_requested_interactions()
    {
        var app = DesignSystemFiles.AppProjectDirectory;
        var overview = File.ReadAllText(Path.Combine(app, "Pages", "ServerOverviewPage.xaml"));
        var settings = File.ReadAllText(Path.Combine(app, "MainWindow.xaml"));
        var buttons = File.ReadAllText(Path.Combine(app, "Themes", "Controls", "Buttons.xaml"));
        var startup = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        var model = File.ReadAllText(Path.Combine(app, "MainViewModel.cs"));

        Assert.Contains("MAXIMUM RAM", overview, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", overview, StringComparison.Ordinal);
        Assert.Contains("OnlinePlayerRows", overview, StringComparison.Ordinal);
        Assert.Contains("AppPlayerHead", overview, StringComparison.Ordinal);
        Assert.Contains("Content=\"Ban\"", overview, StringComparison.Ordinal);
        Assert.Contains("Example: D:\\Minecraft Backups", settings, StringComparison.Ordinal);
        Assert.Contains("AppDurationPressIn", buttons, StringComparison.Ordinal);
        Assert.Contains("MinimizeToTray: true", startup, StringComparison.Ordinal);
        Assert.Contains("private bool minimizeToTray;", model, StringComparison.Ordinal);
    }

    private static string ProfileWithTexture(string url)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { textures = new { SKIN = new { url } } })));
        return JsonSerializer.Serialize(new
        {
            properties = new[] { new { name = "textures", value = payload } }
        });
    }
}
