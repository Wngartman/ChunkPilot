using System.Net;
using System.Net.Sockets;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// Hands out a TCP port that a fixture may listen on, and never the same one twice.
/// </summary>
/// <remarks>
/// <para>
/// An integration fixture must not depend on a port the outside world has opinions about. Minecraft's
/// 25565 is the worst possible choice: it is the first port any real server on the machine takes, and a
/// fixture that defaults to it fails for a reason that has nothing to do with the code under test —
/// which is exactly what CP-2026-014 was.
/// </para>
/// <para>
/// The allocation is the operating system's own: binding port 0 makes the kernel pick a free ephemeral
/// port, which is the only source of truth about what is free. What this adds on top is a record of
/// everything already handed out in this process, so two fixtures running side by side can never be
/// given the same number even if the kernel would reuse it, and a re-probe of the candidate so a port
/// taken between the two calls is discarded rather than returned. Ports Windows has excluded from
/// dynamic allocation cannot be handed out at all, because the kernel never offers them.
/// </para>
/// </remarks>
internal static class TestPortAllocator
{
    /// <summary>Reserved by IANA for Minecraft's default and therefore never usable as a fixture port.</summary>
    private const int MinecraftDefaultPort = 25565;

    private static readonly HashSet<int> Issued = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// Returns a TCP port no other caller in this process has been given, and that was free at the
    /// moment it was chosen.
    /// </summary>
    public static int Reserve()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = Ask();
            if (candidate == MinecraftDefaultPort || !Claim(candidate))
                continue;
            // Asked for twice on purpose: the first call proves the kernel considers it free, the
            // second proves nothing took it in between. A port that fails here is abandoned rather
            // than retried, so a fixture is never handed one that is already in use.
            if (CanBind(candidate))
                return candidate;
        }
        throw new InvalidOperationException("No free TCP port could be reserved for this fixture.");
    }

    private static int Ask()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool Claim(int port)
    {
        lock (Gate)
            return Issued.Add(port);
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
