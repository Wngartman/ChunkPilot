using System.Text;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class NeoForgeLanAdvertisementTests
{
    [Theory]
    [InlineData("advertiseDedicatedServerToLan")]
    [InlineData("\"advertiseDedicatedServerToLan\"")]
    [InlineData("'advertiseDedicatedServerToLan'")]
    public void Changes_only_the_exact_networking_boolean_preserving_bom_comments_and_line_endings(string key)
    {
        var source = "# untouched\r\nremoveErroringEntities = false\r\n" + key + " = true # keep comment\r\npermissionHandler = \"neoforge:default_handler\"\r\n";
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(source)];
        var updated = NeoForgeLanAdvertisement.Disable(bytes);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, updated.Take(3));
        Assert.Equal(source.Replace(key + " = true", key + " = false", StringComparison.Ordinal),
            Encoding.UTF8.GetString(updated.AsSpan(3)));
    }

    [Theory]
    [InlineData("advertiseDedicatedServerToLan = true\nadvertiseDedicatedServerToLan = false\n")]
    [InlineData("advertiseDedicatedServerToLan = \"true\"\n")]
    [InlineData("[other]\nadvertiseDedicatedServerToLan = true\n")]
    [InlineData("note = \"\"\"\nadvertiseDedicatedServerToLan = true\n\"\"\"\n")]
    [InlineData("values = [\ntrue, false\n]\n")]
    public void Ambiguous_or_unsupported_configuration_fails_closed(string source) =>
        Assert.Throws<InvalidDataException>(() => NeoForgeLanAdvertisement.Disable(Encoding.UTF8.GetBytes(source)));

    [Fact]
    public void Missing_key_is_added_without_changing_other_defaults()
    {
        var output = Encoding.UTF8.GetString(NeoForgeLanAdvertisement.Disable(
            Encoding.UTF8.GetBytes("removeErroringEntities = false\n")));
        Assert.StartsWith("removeErroringEntities = false\n", output);
        Assert.Contains("advertiseDedicatedServerToLan = false\n", output);
        Assert.Equal(output, Encoding.UTF8.GetString(NeoForgeLanAdvertisement.Disable(Encoding.UTF8.GetBytes(output))));
    }

    [Fact]
    public async Task New_creation_preserves_pack_defaults_and_validation_uses_only_disposable_override()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-neoforge-network-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaultconfigs"));
        var defaults = Path.Combine(root, "defaultconfigs", "neoforge-server.toml");
        const string original = "removeErroringEntities = false\npermissionHandler = \"custom:keep\"\nadvertiseDedicatedServerToLan = true\n";
        await File.WriteAllTextAsync(defaults, original);
        try
        {
            await NeoForgeLanAdvertisement.DisableForNewCreationAsync(root, "intended-world", default);
            Assert.Equal(original, await File.ReadAllTextAsync(defaults));
            var intended = Path.Combine(root, "config", "neoforge-server.toml");
            Assert.Equal(original.Replace("ToLan = true", "ToLan = false", StringComparison.Ordinal), await File.ReadAllTextAsync(intended));
            await File.WriteAllTextAsync(intended, original); // Existing LAN preference is untouched by validation.
            await NeoForgeLanAdvertisement.PrepareValidationWorldAsync(root, "intended-world", "disposable", default);
            Assert.Equal(original, await File.ReadAllTextAsync(intended));
            Assert.Equal(original, await File.ReadAllTextAsync(defaults));
            Assert.Contains("ToLan = false", await File.ReadAllTextAsync(Path.Combine(root, "disposable", "serverconfig", "neoforge-server.toml")));
            Assert.False(Directory.Exists(Path.Combine(root, "intended-world")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Existing_world_override_wins_and_unrelated_configuration_remains_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-neoforge-network-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "world", "serverconfig"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var config = Path.Combine(root, "config", "neoforge-server.toml");
        var world = Path.Combine(root, "world", "serverconfig", "neoforge-server.toml");
        await File.WriteAllTextAsync(config, "permissionHandler = \"base:keep\"\n");
        await File.WriteAllTextAsync(world, "permissionHandler = \"world:keep\"\nadvertiseDedicatedServerToLan = true\n");
        try
        {
            await NeoForgeLanAdvertisement.DisableForNewCreationAsync(root, "world", default);
            Assert.Equal("permissionHandler = \"base:keep\"\n", await File.ReadAllTextAsync(config));
            Assert.Equal("permissionHandler = \"world:keep\"\nadvertiseDedicatedServerToLan = false\n", await File.ReadAllTextAsync(world));
            await Assert.ThrowsAsync<InvalidDataException>(() => NeoForgeLanAdvertisement.DisableForNewCreationAsync(root, "../../outside", default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
