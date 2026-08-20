using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class VanillaNetworkingPreferenceTests
{
    [Theory]
    [InlineData(VanillaNetworkingPreference.FriendsOverInternet, NetworkMode.PortForwarding)]
    [InlineData(VanillaNetworkingPreference.ThisNetworkOnly, NetworkMode.HomeNetwork)]
    [InlineData(VanillaNetworkingPreference.HomeNetwork, NetworkMode.HomeNetwork)]
    [InlineData(VanillaNetworkingPreference.ThisComputerOnly, NetworkMode.ThisComputerOnly)]
    [InlineData(VanillaNetworkingPreference.DecideLater, NetworkMode.ConfigureLater)]
    public void ToNetworkMode_preserves_each_creation_choice(
        VanillaNetworkingPreference preference,
        NetworkMode expected)
    {
        Assert.Equal(expected, VanillaNetworkingPreferencePolicy.ToNetworkMode(preference));
    }
}
