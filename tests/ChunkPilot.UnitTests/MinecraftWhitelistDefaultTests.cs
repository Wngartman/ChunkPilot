using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class MinecraftWhitelistDefaultTests
{
    [Theory]
    [InlineData("1.7.10", false)]
    [InlineData("1.21.1", false)]
    [InlineData("26.1", false)]
    [InlineData("26.2", false)]
    [InlineData("26.3", true)]
    [InlineData("26.3.1", true)]
    public void Missing_property_uses_the_known_release_default_without_editing_the_document(string version, bool expected)
    {
        const string original = "# Owner configuration\r\nserver-port=25565\r\n";
        var document = ServerPropertiesDocument.Parse(original);
        Assert.Equal(expected,
            MinecraftVersionClassification.ResolveWhitelistEnabled(version, document.Get("white-list")));
        Assert.Equal(original, document.ToString());
    }

    [Theory]
    [InlineData("26.3", "false", false)]
    [InlineData("26.3", "true", true)]
    [InlineData("26.2", "TRUE", true)]
    [InlineData("Unknown", "false", false)]
    [InlineData("Unknown", "true", true)]
    [InlineData("26.3", "", false)]
    public void Explicit_values_override_the_default(string version, string configured, bool expected)
    {
        Assert.Equal(expected, MinecraftVersionClassification.ResolveWhitelistEnabled(version, configured));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("26.3-snapshot-1")]
    [InlineData("26.3-rc1")]
    [InlineData("27.1")]
    [InlineData("b1.8.1")]
    public void Unestablished_defaults_remain_unknown(string version)
    {
        Assert.Null(MinecraftVersionClassification.ResolveWhitelistEnabled(version, null));
    }
}
