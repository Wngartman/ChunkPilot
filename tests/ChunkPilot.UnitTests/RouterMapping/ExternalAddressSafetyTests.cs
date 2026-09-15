using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.RouterMapping;

public sealed class ExternalAddressSafetyTests
{
    [Theory]
    [InlineData("::ffff:192.168.1.2", RoutableAddressClass.PrivateUse)]
    [InlineData("::ffff:100.64.0.1", RoutableAddressClass.SharedAddressSpace)]
    [InlineData("ff02::1", RoutableAddressClass.Reserved)]
    [InlineData("fec0::1", RoutableAddressClass.PrivateUse)]
    [InlineData("3fff:1::1", RoutableAddressClass.Documentation)]
    [InlineData("100::1", RoutableAddressClass.Reserved)]
    [InlineData("2001:2::1", RoutableAddressClass.Reserved)]
    public void Special_purpose_addresses_do_not_become_public_endpoints(string address, RoutableAddressClass expected)
    {
        var result = RouterMappingPolicy.ClassifyExternalAddress(address);
        Assert.Equal(expected, result.Class);
    }
}
