using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public static class ServerConnectionObserver
{
    public static async Task<ServerBindingEvidence> ReadSavedAsync(ServerDefinition server, SafeFileService files, CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            if (server.GameKind != ServerGameKind.Minecraft || server.Ecosystem is ServerEcosystem.Unknown or ServerEcosystem.Custom ||
                !Path.GetFullPath(server.RootPath).Equals(Path.GetFullPath(server.WorkingDirectory), StringComparison.OrdinalIgnoreCase))
                return new() { CheckedAt = checkedAt, Detail = "This launch profile's effective configuration cannot be inferred from server.properties." };
            var path = files.ResolveWithinRoot(server.RootPath, "server.properties", mustExist: true);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            const int limit = 64 * 1024;
            if (stream.Length > limit) throw new InvalidDataException("Binding file exceeds the bounded observation limit.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != bytes.Length || bytes.Contains((byte)0)) throw new InvalidDataException("Binding file changed or has an unsupported encoding.");
            var document = ServerPropertiesDocument.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            var bind = document.Get("server-ip") ?? "";
            if (bind.Length > 0 && !IPAddress.TryParse(bind, out _)) throw new InvalidDataException("The saved bind address needs explicit resolution.");
            if (!int.TryParse(document.Get("server-port") ?? "25565", NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new InvalidDataException("The saved game port is not valid.");
            return new() { Known = true, BindAddress = bind, Port = port, CheckedAt = checkedAt,
                Detail = "Read from server.properties; this is saved configuration, not runtime reachability." };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new() { CheckedAt = checkedAt, Detail = "The saved binding could not be safely read or interpreted." };
        }
    }

    /// <summary>Observe only the handle of the process actually launched by this ManagedServer. Never adopt a PID from a port table.</summary>
    public static ServerListenerEvidence Observe(Process owned, int port)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || owned.HasExited) return new();
            var handle = owned.SafeHandle;
            var creation = ProcessCreationIdentity.Of(handle);
            if (creation == 0) return new();
            var pid = owned.Id;
            var first = WindowsStagedNetworkEndpoints.CaptureTcp().Where(endpoint => endpoint.ProcessId == pid && endpoint.Port == port && endpoint.IsTcpListener)
                .Select(endpoint => endpoint.LocalAddress.ToString()).Order(StringComparer.Ordinal).ToArray();
            var second = WindowsStagedNetworkEndpoints.CaptureTcp().Where(endpoint => endpoint.ProcessId == pid && endpoint.Port == port && endpoint.IsTcpListener)
                .Select(endpoint => endpoint.LocalAddress.ToString()).Order(StringComparer.Ordinal).ToArray();
            if (owned.HasExited || !ProcessCreationIdentity.Matches(creation, ProcessCreationIdentity.Of(handle)) || !first.SequenceEqual(second)) return new();
            return new() { ProcessId = pid, ProcessCreationTicks = creation, CheckedAt = DateTimeOffset.UtcNow,
                ExactOwnerVerified = true, BindAddresses = second.Distinct().ToArray(), Port = port,
                Detail = "Stable TCP LISTEN records bracketed by the held exact root-process handle. Wrapper/descendant listeners are not inferred." };
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or InvalidDataException or NotSupportedException)
        {
            return new() { Detail = "Windows did not supply stable exact-owned listener evidence." };
        }
    }
}
