using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class MinecraftStatusClientTests
{
    [Fact]
    public async Task Query_falls_back_to_the_bounded_legacy_server_list_protocol()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using (var modern = await listener.AcceptTcpClientAsync())
            {
                // The first connection is the modern status attempt. Closing it forces the explicit
                // legacy fallback without emulating an unrelated modern protocol fixture here.
            }
            using var legacy = await listener.AcceptTcpClientAsync();
            await using var stream = legacy.GetStream();
            var request = new byte[2];
            await stream.ReadExactlyAsync(request);
            Assert.Equal(new byte[] { 0xFE, 0x01 }, request);
            var value = "§1\0" + "78\0" + "1.6.4\0" + "Fixture\0" + "2\0" + "12";
            var payload = Encoding.BigEndianUnicode.GetBytes(value);
            var response = new byte[payload.Length + 3];
            response[0] = 0xFF;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1, 2), checked((ushort)value.Length));
            payload.CopyTo(response, 3);
            await stream.WriteAsync(response);
        });

        try
        {
            var result = await new MinecraftStatusClient().QueryAsync("127.0.0.1", port);
            Assert.Equal((2, 12), result);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task QueryDetailed_uses_the_pre_1_4_ping_first_for_historical_versions()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var legacy = await listener.AcceptTcpClientAsync();
            await using var stream = legacy.GetStream();
            var request = new byte[1];
            await stream.ReadExactlyAsync(request);
            Assert.Equal(new byte[] { 0xFE }, request);
            var value = "Fixture MOTD§3§20";
            var payload = Encoding.BigEndianUnicode.GetBytes(value);
            var response = new byte[payload.Length + 3];
            response[0] = 0xFF;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1, 2), checked((ushort)value.Length));
            payload.CopyTo(response, 3);
            await stream.WriteAsync(response);
        });

        try
        {
            var result = await new MinecraftStatusClient()
                .QueryDetailedAsync("127.0.0.1", port, "1.2.5");
            Assert.NotNull(result);
            Assert.Equal(PlayerStatusSource.LegacySimpleStatus, result.Source);
            Assert.True(result.Exact);
            Assert.Equal(3, result.Online);
            Assert.Equal(20, result.Maximum);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.2.5")]
    [InlineData("b1.8.1")]
    [InlineData("a1.2.6")]
    public void Historical_versions_prefer_the_simple_legacy_ping(string version)
    {
        Assert.True(MinecraftStatusClient.PrefersSimpleLegacyPing(version));
    }
}
