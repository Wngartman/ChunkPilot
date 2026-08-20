using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Sends one datagram from a chosen local address and waits for the gateway's answer.
/// </summary>
/// <remarks>
/// The socket is bound to the selected LAN address rather than to any address, so a request can never
/// leave through a VPN or virtual adapter. Datagrams from anything other than the gateway are discarded:
/// on a home network any host can send to an open UDP port, and none of them are the router.
/// </remarks>
public sealed class UdpGatewayDatagramChannel : IGatewayDatagramChannel
{
    public async Task<byte[]?> ExchangeAsync(
        IPAddress localAddress,
        IPEndPoint gateway,
        byte[] payload,
        TimeSpan timeout,
        Action? onSent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(payload);
        using var client = new UdpClient(new IPEndPoint(localAddress, 0));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var bytesSent = await client.SendAsync(payload, gateway, deadline.Token).ConfigureAwait(false);
            if (!ConfirmCompleteDatagram(bytesSent, payload.Length, onSent))
                return null;
            while (!deadline.Token.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (received.RemoteEndPoint.Address.Equals(gateway.Address))
                    return received.Buffer;
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms a send only when the socket accepted the entire datagram. Kept as the smallest internal
    /// seam because a real UDP socket does not offer a deterministic way to produce a successful short
    /// send in a regression test.
    /// </summary>
    internal static bool ConfirmCompleteDatagram(int bytesSent, int payloadLength, Action? onSent)
    {
        if (bytesSent != payloadLength)
            return false;

        // Nothing stronger exists for UDP, which acknowledges nothing. A completed full-length send is
        // the platform's confirmation that the complete datagram was handed to the socket.
        onSent?.Invoke();
        return true;
    }
}

/// <summary>
/// One bounded SSDP M-SEARCH, following the UPnP Device Architecture: HTTPU to 239.255.255.250:1900
/// with the required HOST, MAN and MX headers, collecting unicast answers until MX elapses.
/// </summary>
public sealed class SsdpSearchChannel : ISsdpSearchChannel
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int MulticastPort = 1900;

    public async Task<IReadOnlyList<SsdpDiscoveryResponse>> SearchAsync(
        IPAddress localAddress,
        IReadOnlyList<string> searchTargets,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(searchTargets);
        var seconds = Math.Clamp((int)Math.Round(maximumWait.TotalSeconds), 1, 5);
        var results = new List<SsdpDiscoveryResponse>();
        using var client = new UdpClient(new IPEndPoint(localAddress, 0));
        // The architecture recommends a small TTL for discovery; the router is one hop away.
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        var destination = new IPEndPoint(MulticastAddress, MulticastPort);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 1));
        try
        {
            foreach (var target in searchTargets)
            {
                var request =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    $"MX: {seconds.ToString(CultureInfo.InvariantCulture)}\r\n" +
                    $"ST: {target}\r\n" +
                    "USER-AGENT: Windows/10.0 UPnP/1.0 ChunkPilot/1.3\r\n" +
                    "\r\n";
                await client.SendAsync(Encoding.ASCII.GetBytes(request), destination, deadline.Token)
                    .ConfigureAwait(false);
            }
            while (!deadline.Token.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var parsed = Parse(Encoding.ASCII.GetString(received.Buffer), received.RemoteEndPoint.Address);
                if (parsed is not null)
                    results.Add(parsed);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (SocketException) { }
        return results;
    }

    internal static SsdpDiscoveryResponse? Parse(string message, IPAddress source)
    {
        var lines = message.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return headers.TryGetValue("LOCATION", out var location) && location.Length > 0
            ? new SsdpDiscoveryResponse(
                headers.GetValueOrDefault("ST", ""),
                location,
                headers.GetValueOrDefault("USN", ""),
                headers.GetValueOrDefault("SERVER", ""),
                source)
            : null;
    }
}

/// <summary>
/// SOAP control over HTTP, following the UPnP Device Architecture control section: POST with a
/// SOAPACTION header of "serviceType#actionName", a UTF-8 envelope body, and UPnPError faults parsed
/// out of an HTTP 500 response.
/// </summary>
public sealed class UpnpControlChannel : IUpnpControlChannel, IDisposable
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Control = "urn:schemas-upnp-org:control-1-0";

    private readonly HttpClient http;
    private readonly bool ownsHttpClient;

    public UpnpControlChannel(RouterMappingOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ownsHttpClient = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = options.HttpTimeout };
    }

    public async Task<string> GetDescriptionAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpnpSoapResponse> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        IReadOnlyList<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var body = BuildEnvelope(serviceType, action, arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "text/xml")
        };
        request.Content.Headers.ContentType!.CharSet = "utf-8";
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseResponse(action, response.IsSuccessStatusCode, text);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new UpnpSoapResponse(false, new Dictionary<string, string>(StringComparer.Ordinal), 0,
                exception.Message);
        }
    }

    internal static string BuildEnvelope(
        string serviceType, string action, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\"?>");
        builder.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        builder.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        builder.Append(CultureInfo.InvariantCulture, $"<u:{action} xmlns:u=\"{Escape(serviceType)}\">");
        foreach (var argument in arguments)
            builder.Append(CultureInfo.InvariantCulture,
                $"<{argument.Key}>{Escape(argument.Value)}</{argument.Key}>");
        builder.Append(CultureInfo.InvariantCulture, $"</u:{action}></s:Body></s:Envelope>");
        return builder.ToString();
    }

    internal static UpnpSoapResponse ParseResponse(string action, bool httpSuccess, string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException exception)
        {
            return new UpnpSoapResponse(false, new Dictionary<string, string>(StringComparer.Ordinal), 0,
                $"The gateway's answer to {action} was not readable XML: {exception.Message}");
        }

        var fault = document.Descendants(Control + "UPnPError").FirstOrDefault();
        if (fault is not null)
        {
            var code = int.TryParse(fault.Element(Control + "errorCode")?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            return new UpnpSoapResponse(false, new Dictionary<string, string>(StringComparer.Ordinal), code,
                fault.Element(Control + "errorDescription")?.Value ?? "");
        }
        if (!httpSuccess)
            return new UpnpSoapResponse(false, new Dictionary<string, string>(StringComparer.Ordinal), 0,
                $"The gateway rejected {action} without a UPnPError body.");

        var response = document.Descendants(Soap + "Body").Elements().FirstOrDefault();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response is not null)
            foreach (var element in response.Elements())
                values[element.Name.LocalName] = element.Value;
        return new UpnpSoapResponse(true, values, 0, "");
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    public void Dispose()
    {
        if (ownsHttpClient)
            http.Dispose();
    }
}
