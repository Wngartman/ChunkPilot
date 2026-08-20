using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class MinecraftStatusClient
{
    public async Task<(int Online, int Maximum)?> QueryAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var evidence = await QueryDetailedAsync(host, port, minecraftVersion: null, cancellationToken)
            .ConfigureAwait(false);
        return evidence?.Online is { } online && evidence.Maximum is { } maximum
            ? (online, maximum)
            : null;
    }

    public async Task<PlayerStatusEvidence?> QueryDetailedAsync(
        string host,
        int port,
        string? minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (PrefersSimpleLegacyPing(minecraftVersion))
        {
            if (await TryQueryLegacySimpleAsync(host, port, cancellationToken).ConfigureAwait(false) is { } simple)
                return Evidence(simple, PlayerStatusSource.LegacySimpleStatus,
                    "Exact count from the pre-1.4 legacy server-list ping.");
            if (await TryQueryLegacyExtendedAsync(host, port, cancellationToken).ConfigureAwait(false) is { } extended)
                return Evidence(extended, PlayerStatusSource.LegacyExtendedStatus,
                    "Exact count from the extended legacy server-list ping.");
            if (await TryQueryModernAsync(host, port, cancellationToken).ConfigureAwait(false) is { } modern)
                return Evidence(modern, PlayerStatusSource.ModernStatus,
                    "Exact count from the modern Minecraft status protocol.");
            return null;
        }

        if (await TryQueryModernAsync(host, port, cancellationToken).ConfigureAwait(false) is { } modernFirst)
            return Evidence(modernFirst, PlayerStatusSource.ModernStatus,
                "Exact count from the modern Minecraft status protocol.");
        if (await TryQueryLegacyExtendedAsync(host, port, cancellationToken).ConfigureAwait(false) is { } extendedFallback)
            return Evidence(extendedFallback, PlayerStatusSource.LegacyExtendedStatus,
                "Exact count from the extended legacy server-list ping.");
        if (await TryQueryLegacySimpleAsync(host, port, cancellationToken).ConfigureAwait(false) is { } simpleFallback)
            return Evidence(simpleFallback, PlayerStatusSource.LegacySimpleStatus,
                "Exact count from the pre-1.4 legacy server-list ping.");
        return null;
    }

    private static PlayerStatusEvidence Evidence(
        (int Online, int Maximum) value,
        PlayerStatusSource source,
        string detail) => new()
    {
        Online = value.Online,
        Maximum = value.Maximum,
        Source = source,
        Exact = true,
        CheckedAt = DateTimeOffset.UtcNow,
        Detail = detail
    };

    private static async Task<(int Online, int Maximum)?> TryQueryModernAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, 0);
            var hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(handshake, hostBytes.Length);
            handshake.Write(hostBytes);
            Span<byte> portBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), cancellationToken).ConfigureAwait(false);
            await WritePacketAsync(stream, [0], cancellationToken).ConfigureAwait(false);
            _ = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
            _ = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
            var jsonLength = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
            if (jsonLength is <= 0 or > 1_048_576)
                return null;
            var jsonBytes = new byte[jsonLength];
            await stream.ReadExactlyAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(jsonBytes);
            var players = document.RootElement.GetProperty("players");
            return (players.GetProperty("online").GetInt32(), players.GetProperty("max").GetInt32());
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or
                                                  JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<(int Online, int Maximum)?> TryQueryLegacyExtendedAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0xFE, 0x01 }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLegacyResponseAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or
                                                  InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<(int Online, int Maximum)?> TryQueryLegacySimpleAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0xFE }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLegacyResponseAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or
                                                  InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<(int Online, int Maximum)?> ReadLegacyResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0xFF)
            return null;
        var characterCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (characterCount is 0 or > 32_767)
            return null;
        var payload = new byte[characterCount * 2];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var response = Encoding.BigEndianUnicode.GetString(payload);
        var extended = response.Split('\0');
        if (extended.Length >= 6 && extended[0] == "§1" &&
            int.TryParse(extended[4], out var extendedOnline) &&
            int.TryParse(extended[5], out var extendedMaximum) &&
            ValidCounts(extendedOnline, extendedMaximum))
            return (extendedOnline, extendedMaximum);
        var legacy = response.Split('§');
        if (legacy.Length >= 3 &&
            int.TryParse(legacy[^2], out var legacyOnline) &&
            int.TryParse(legacy[^1], out var legacyMaximum) &&
            ValidCounts(legacyOnline, legacyMaximum))
            return (legacyOnline, legacyMaximum);
        return null;
    }

    private static bool ValidCounts(int online, int maximum) => online >= 0 && maximum >= 0 && online <= maximum;

    internal static bool PrefersSimpleLegacyPing(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return false;
        if (minecraftVersion.StartsWith('a') || minecraftVersion.StartsWith('b') ||
            minecraftVersion.StartsWith("rd-", StringComparison.OrdinalIgnoreCase) ||
            minecraftVersion.StartsWith("c0.", StringComparison.OrdinalIgnoreCase))
            return true;
        return Version.TryParse(minecraftVersion, out var version) && version < new Version(1, 4);
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value = (int)((uint)value >> 7);
            if (value != 0)
                current |= 0x80;
            stream.WriteByte(current);
        } while (value != 0);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        var position = 0;
        while (position < 35)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            value |= (buffer[0] & 0x7F) << position;
            if ((buffer[0] & 0x80) == 0)
                return value;
            position += 7;
        }
        throw new InvalidDataException("Minecraft VarInt is too long.");
    }
}

