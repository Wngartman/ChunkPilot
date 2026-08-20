using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// A controlled gateway on the loopback interface that answers real PCP and NAT-PMP datagrams.
/// </summary>
/// <remarks>
/// Deliberately a real UDP socket speaking the real wire format rather than a mocked provider: the
/// point of these tests is that ChunkPilot's bytes match RFC 6886 and RFC 6887. It binds to
/// 127.0.0.1 on an ephemeral port and can only ever be reached from this machine, so no test can
/// touch a real router.
/// </remarks>
internal sealed class FakeDatagramGateway : IAsyncDisposable
{
    private readonly UdpClient socket;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;
    private readonly List<byte[]> received = [];
    private readonly object gate = new();

    private FakeDatagramGateway(UdpClient socket, Func<byte[], byte[]?> responder)
    {
        this.socket = socket;
        Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        loop = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.Token.IsCancellationRequested)
                {
                    var request = await socket.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                    lock (gate)
                        received.Add(request.Buffer);
                    var reply = responder(request.Buffer);
                    if (reply is not null)
                        await socket.SendAsync(reply, request.RemoteEndPoint, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                  or ObjectDisposedException) { }
        });
    }

    public int Port { get; }

    public IReadOnlyList<byte[]> Received
    {
        get { lock (gate) return received.ToArray(); }
    }

    public static FakeDatagramGateway Start(Func<byte[], byte[]?> responder) =>
        new(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)), responder);

    /// <summary>A gateway that answers nothing, for timeout coverage.</summary>
    public static FakeDatagramGateway Silent() => Start(_ => null);

    public RouterLanBinding Binding() =>
        new("loopback-fixture", "Fixture", IPAddress.Loopback, 8, IPAddress.Loopback);

    public RouterMappingOptions Options(int attempts = 2, TimeSpan? attemptTimeout = null) => new()
    {
        GatewayControlPort = Port,
        // These tests intentionally use a real loopback UDP socket. Hosted Windows runners can
        // occasionally leave its responder task unscheduled for longer than 300 ms, so response
        // fixtures need a realistic window. Tests that exercise silence pass the shorter timeout
        // explicitly and retain their bounded retry assertions.
        DatagramAttemptTimeouts = Enumerable.Repeat(
            attemptTimeout ?? TimeSpan.FromSeconds(2), attempts).ToArray(),
        DiscoveryBudget = TimeSpan.FromSeconds(10),
        OperationBudget = TimeSpan.FromSeconds(10)
    };

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        socket.Dispose();
        try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}

/// <summary>Builds the exact NAT-PMP frames of RFC 6886 so tests assert against the real format.</summary>
internal static class NatPmp
{
    /// <summary>The Seconds Since Start of Epoch a fixture reports unless a test is exercising it.</summary>
    public const uint DefaultEpoch = 1234;

    public static byte[] AddressReply(ushort resultCode, string externalAddress, uint epochSeconds = DefaultEpoch)
    {
        var reply = new byte[12];
        reply[0] = 0;
        reply[1] = 128;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), resultCode);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), epochSeconds);
        IPAddress.Parse(externalAddress).GetAddressBytes().CopyTo(reply.AsSpan(8, 4));
        return reply;
    }

    public static byte[] MapReply(
        byte requestOpcode,
        ushort resultCode,
        int internalPort,
        int externalPort,
        uint lifetime,
        uint epochSeconds = DefaultEpoch)
    {
        var reply = new byte[16];
        reply[0] = 0;
        reply[1] = (byte)(128 + requestOpcode);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), resultCode);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), epochSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8, 2), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12, 4), lifetime);
        return reply;
    }

    /// <summary>The epoch a reply carried, as a client would read it.</summary>
    public static uint Epoch(byte[] reply) => BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4));

    public static bool IsAddressRequest(byte[] request) => request.Length == 2 && request[1] == 0;

    public static byte Opcode(byte[] request) => request[1];

    public static int InternalPort(byte[] request) =>
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4, 2));

    public static int SuggestedExternalPort(byte[] request) =>
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6, 2));

    public static uint Lifetime(byte[] request) =>
        BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(8, 4));
}

/// <summary>Builds the exact PCP frames of RFC 6887 so tests assert against the real format.</summary>
internal static class Pcp
{
    public const int HeaderLength = 24;

    /// <summary>The Epoch Time a fixture reports unless a test is exercising it.</summary>
    public const uint DefaultEpoch = 99;

    public static byte[] AnnounceReply(byte resultCode = 0, byte version = 2, uint epochSeconds = DefaultEpoch)
    {
        var reply = new byte[HeaderLength];
        reply[0] = version;
        reply[1] = 0x80;
        reply[3] = resultCode;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8, 4), epochSeconds);
        return reply;
    }

    public static byte[] MapReply(
        byte[] request,
        byte resultCode,
        uint lifetime,
        int externalPort,
        string externalAddress,
        uint epochSeconds = DefaultEpoch)
    {
        var reply = new byte[HeaderLength + 36];
        reply[0] = 2;
        reply[1] = 0x81;
        reply[3] = resultCode;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), lifetime);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8, 4), epochSeconds);
        request.AsSpan(HeaderLength, 12).CopyTo(reply.AsSpan(HeaderLength, 12));
        reply[HeaderLength + 12] = request[HeaderLength + 12];
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(HeaderLength + 16, 2),
            BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(HeaderLength + 16, 2)));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(HeaderLength + 18, 2), (ushort)externalPort);
        WriteMapped(reply.AsSpan(HeaderLength + 20, 16), IPAddress.Parse(externalAddress));
        return reply;
    }

    public static bool IsAnnounce(byte[] request) => request.Length == HeaderLength && request[1] == 0;

    public static bool IsMap(byte[] request) => request.Length >= HeaderLength + 36 && request[1] == 1;

    public static byte Protocol(byte[] request) => request[HeaderLength + 12];

    public static int InternalPort(byte[] request) =>
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(HeaderLength + 16, 2));

    public static int SuggestedExternalPort(byte[] request) =>
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(HeaderLength + 18, 2));

    public static uint RequestedLifetime(byte[] request) =>
        BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4));

    public static string Nonce(byte[] request) =>
        Convert.ToHexString(request.AsSpan(HeaderLength, 12));

    public static byte Version(byte[] request) => request[0];

    /// <summary>The Epoch Time a reply carried, as a client would read it.</summary>
    public static uint Epoch(byte[] reply) => BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8, 4));

    private static void WriteMapped(Span<byte> destination, IPAddress address)
    {
        destination.Clear();
        destination[10] = 0xFF;
        destination[11] = 0xFF;
        address.GetAddressBytes().CopyTo(destination[12..]);
    }
}

/// <summary>
/// A controlled UPnP Internet Gateway Device on loopback HTTP: it serves a real device description and
/// answers real SOAP envelopes, so the provider's XML and SOAPACTION handling is genuinely exercised.
/// </summary>
internal sealed class FakeUpnpGateway : IAsyncDisposable
{
    public const string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";

    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;

    public FakeUpnpGateway()
    {
        var port = FreeTcpPort();
        Prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Prefix);
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    public string Prefix { get; }
    public string DescriptionUrl => $"{Prefix}rootDesc.xml";
    public string ExternalAddress { get; set; } = "203.0.113.10";

    /// <summary>The mapping table the fixture reports, keyed by "PROTOCOL:port".</summary>
    public Dictionary<string, ExistingRouterMapping> Mappings { get; } = new(StringComparer.Ordinal);

    /// <summary>Forces a specific UPnP error from AddPortMapping. 0 means accept.</summary>
    public int AddErrorCode { get; set; }

    /// <summary>
    /// Decides an AddPortMapping error from the request body, for gateways whose refusal depends on
    /// what was asked — a router that only accepts permanent entries, for example.
    /// </summary>
    public Func<string, int>? AddErrorSelector { get; set; }

    /// <summary>Forces a specific UPnP error from DeletePortMapping. 0 means accept.</summary>
    public int DeleteErrorCode { get; set; }

    /// <summary>Answers AddPortMapping with an HTTP failure that carries no UPnPError body.</summary>
    public bool AddReturnsBodylessHttpFailure { get; set; }

    /// <summary>Stalls every control response, for timeout coverage.</summary>
    public TimeSpan ResponseDelay { get; set; }

    /// <summary>Every action invoked, in order.</summary>
    public List<string> Actions { get; } = [];

    public RouterLanBinding Binding() =>
        new("loopback-fixture", "Fixture", IPAddress.Loopback, 8, IPAddress.Loopback);

    public SsdpDiscoveryResponse Response() =>
        new(ServiceType, DescriptionUrl, "uuid:fixture::" + ServiceType, "Fixture/1.0 UPnP/1.0", IPAddress.Loopback);

    public UpnpIgdMappingProvider Provider(ISsdpSearchChannel? ssdp = null, RouterMappingOptions? options = null)
    {
        var settings = options ?? new RouterMappingOptions { SsdpMaximumWait = TimeSpan.FromMilliseconds(200) };
        return new UpnpIgdMappingProvider(
            ssdp ?? new StubSsdpChannel([Response()]),
            new UpnpControlChannel(settings),
            settings);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.Token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var body = context.Request.HttpMethod == "POST"
                    ? await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync()
                        .ConfigureAwait(false)
                    : "";
                if (ResponseDelay > TimeSpan.Zero)
                    await Task.Delay(ResponseDelay, lifetime.Token).ConfigureAwait(false);
                var (status, payload) = Handle(context.Request, body);
                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                context.Response.Close();
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException
                                              or OperationCanceledException) { }
    }

    private (int Status, string Body) Handle(HttpListenerRequest request, string body)
    {
        if (request.HttpMethod == "GET")
            return (200, Description());
        var action = (request.Headers["SOAPACTION"] ?? "").Trim('"').Split('#').LastOrDefault() ?? "";
        Actions.Add(action);
        return action switch
        {
            "GetExternalIPAddress" => (200, Ok("GetExternalIPAddress",
                [new KeyValuePair<string, string>("NewExternalIPAddress", ExternalAddress)])),
            "GetSpecificPortMappingEntry" => Specific(body),
            "AddPortMapping" => Add(body),
            "DeletePortMapping" => Delete(body),
            _ => (500, Fault(401, "InvalidAction"))
        };
    }

    private (int, string) Specific(string body)
    {
        var key = $"{Argument(body, "NewProtocol")}:{Argument(body, "NewExternalPort")}";
        if (!Mappings.TryGetValue(key, out var existing))
            return (500, Fault(714, "NoSuchEntryInArray"));
        return (200, Ok("GetSpecificPortMappingEntry",
        [
            new KeyValuePair<string, string>("NewInternalPort", existing.InternalPort.ToString()),
            new KeyValuePair<string, string>("NewInternalClient", existing.InternalClient),
            new KeyValuePair<string, string>("NewEnabled", existing.Enabled ? "1" : "0"),
            new KeyValuePair<string, string>("NewPortMappingDescription", existing.Description),
            new KeyValuePair<string, string>("NewLeaseDuration", existing.LeaseSeconds.ToString())
        ]));
    }

    private (int, string) Add(string body)
    {
        if (AddReturnsBodylessHttpFailure)
            return (500, "<html><body>Internal error</body></html>");
        var error = AddErrorSelector?.Invoke(body) ?? AddErrorCode;
        if (error != 0)
            return (500, Fault(error, UpnpIgdMappingProvider.UpnpErrorName(error)));
        var protocol = Argument(body, "NewProtocol");
        var externalPort = int.Parse(Argument(body, "NewExternalPort"));
        Mappings[$"{protocol}:{externalPort}"] = new ExistingRouterMapping
        {
            ExternalPort = externalPort,
            Transport = protocol == "TCP" ? MappingTransport.Tcp : MappingTransport.Udp,
            InternalClient = Argument(body, "NewInternalClient"),
            InternalPort = int.Parse(Argument(body, "NewInternalPort")),
            Description = Argument(body, "NewPortMappingDescription"),
            Enabled = Argument(body, "NewEnabled") == "1",
            LeaseSeconds = int.Parse(Argument(body, "NewLeaseDuration"))
        };
        return (200, Ok("AddPortMapping", []));
    }

    private (int, string) Delete(string body)
    {
        if (DeleteErrorCode != 0)
            return (500, Fault(DeleteErrorCode, UpnpIgdMappingProvider.UpnpErrorName(DeleteErrorCode)));
        var key = $"{Argument(body, "NewProtocol")}:{Argument(body, "NewExternalPort")}";
        if (!Mappings.Remove(key))
            return (500, Fault(714, "NoSuchEntryInArray"));
        return (200, Ok("DeletePortMapping", []));
    }

    internal static string Argument(string body, string name)
    {
        var open = body.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (open < 0)
            return "";
        open += name.Length + 2;
        var close = body.IndexOf($"</{name}>", open, StringComparison.Ordinal);
        return close < 0 ? "" : body[open..close];
    }

    private string Description() => $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>Fixture Gateway</friendlyName>
            <deviceList><device>
              <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
              <deviceList><device>
                <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                <serviceList><service>
                  <serviceType>{ServiceType}</serviceType>
                  <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                  <SCPDURL>/scpd.xml</SCPDURL>
                  <controlURL>/ctl/IPConn</controlURL>
                  <eventSubURL>/evt/IPConn</eventSubURL>
                </service></serviceList>
              </device></deviceList>
            </device></deviceList>
          </device>
        </root>
        """;

    private static string Ok(string action, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\"?>");
        builder.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>");
        builder.Append($"<u:{action}Response xmlns:u=\"{ServiceType}\">");
        foreach (var value in values)
            builder.Append($"<{value.Key}>{value.Value}</{value.Key}>");
        builder.Append($"</u:{action}Response></s:Body></s:Envelope>");
        return builder.ToString();
    }

    private static string Fault(int code, string description) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
          <faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>
          <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
            <errorCode>{code}</errorCode><errorDescription>{description}</errorDescription>
          </UPnPError></detail>
        </s:Fault></s:Body></s:Envelope>
        """;

    private static int FreeTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private bool disposed;

    /// <summary>Idempotent: a test may stop the gateway mid-way and still dispose it at the end.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener.Close();
        try { await loop.ConfigureAwait(false); } catch (Exception) { }
        lifetime.Dispose();
    }
}

/// <summary>Returns a fixed set of SSDP answers so discovery can be driven without multicast.</summary>
internal sealed class StubSsdpChannel(IReadOnlyList<SsdpDiscoveryResponse> responses) : ISsdpSearchChannel
{
    public int Searches { get; private set; }

    public Task<IReadOnlyList<SsdpDiscoveryResponse>> SearchAsync(
        IPAddress localAddress,
        IReadOnlyList<string> searchTargets,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        Searches++;
        return Task.FromResult(responses);
    }
}

/// <summary>
/// A clock a test drives itself, so epoch validation can be exercised over hours without waiting.
/// </summary>
internal sealed class FixtureClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan amount) => now += amount;
}

internal static class RouterFixtures
{
    public static RouterMappingService Service(
        IRouterNetworkView network,
        RouterMappingOptions options,
        params IRouterMappingProvider[] providers) =>
        new(network, providers, options, NullLogger<RouterMappingService>.Instance);

    public static IRouterNetworkView Loopback() => new SingleBindingView();

    private sealed class SingleBindingView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
        [
            new(new LanInterfaceCandidate(
                    "eth", "Ethernet", "Fixture Ethernet",
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                    System.Net.NetworkInformation.OperationalStatus.Up,
                    1_000, true, true,
                    [new LanAddressCandidate(IPAddress.Parse("192.168.1.50"), 24)]),
                [IPAddress.Parse("192.168.1.1")])
        ];
    }
}
