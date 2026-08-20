using System.Net;
using System.Net.Sockets;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Opens the probe client's HTTPS connection over the address family of the endpoint being verified.
/// </summary>
/// <remarks>
/// <para>
/// A check of an IPv4 router mapping only means something when the service sees the request arrive
/// from the IPv4 address the router reports. On a dual-stack computer the ordinary transport is free
/// to prefer IPv6, and then the service compares an IPv6 source with an IPv4 expectation, concludes
/// nothing, and a correctly forwarded server cannot be verified at all. This is the whole of the fix:
/// the connection is opened over the family that is being verified, or it is not opened.
/// </para>
/// <para>
/// Deliberately small. It resolves one hostname, keeps the addresses of one family, and opens one
/// socket. It does not touch the request URI, the TLS host, the authority or the proxy setting — the
/// handler still performs TLS itself against the configured hostname over the stream returned here,
/// so certificate validation, SNI and the Host header are exactly what they were.
/// </para>
/// </remarks>
public sealed class ExternalProbeTransport
{
    /// <summary>
    /// The family this connection must use, set per request by the probe client. Carried on the
    /// request rather than baked into the transport so the choice stays explicit and per-check, and so
    /// nothing here is a process-wide switch.
    /// </summary>
    public static readonly HttpRequestOptionsKey<AddressFamily> RequiredFamily =
        new("chunkpilot.external-probe.address-family");

    /// <summary>
    /// How many addresses of the required family one connection may try. A service behind several A
    /// records should not fail because the first one is having a bad day, and a long list must not
    /// become an unbounded connect loop.
    /// </summary>
    public const int MaximumAddressAttempts = 3;

    /// <summary>
    /// Bounded per address, so one black hole cannot consume the whole connect budget and leave the
    /// remaining addresses untried.
    /// </summary>
    public static readonly TimeSpan AddressConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly IExternalProbeAddressResolver resolver;
    private readonly IExternalProbeSocketConnector connector;

    public ExternalProbeTransport(
        IExternalProbeAddressResolver? resolver = null, IExternalProbeSocketConnector? connector = null)
    {
        this.resolver = resolver ?? new SystemProbeAddressResolver();
        this.connector = connector ?? new TcpProbeSocketConnector();
    }

    /// <summary>The <see cref="SocketsHttpHandler.ConnectCallback"/> entry point.</summary>
    /// <remarks>
    /// A connection with no declared family is refused rather than opened over whatever comes first:
    /// the point of this class is that the family is a decision, and an absent decision is a defect.
    /// </remarks>
    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.InitialRequestMessage.Options.TryGetValue(RequiredFamily, out var family))
            throw new ExternalProbeTransportException(
                "An external probe connection was attempted without an address family, so nothing was sent.");
        return await ConnectAsync(family, context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the host, keeps only <paramref name="family"/>, and connects to one of them.</summary>
    public async ValueTask<Stream> ConnectAsync(
        AddressFamily family, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<IPAddress> resolved;
        try
        {
            resolved = await resolver.ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new ExternalProbeTransportException(
                "The external probe service's address could not be looked up.", exception);
        }

        // Everything the resolver returned is read and then filtered, so the family that is used never
        // depends on whether A or AAAA records happened to be listed first.
        var candidates = new List<IPEndPoint>(MaximumAddressAttempts);
        foreach (var address in resolved)
        {
            if (address.AddressFamily != family)
                continue;
            candidates.Add(new IPEndPoint(address, endpoint.Port));
            if (candidates.Count == MaximumAddressAttempts)
                break;
        }

        // Falling back to the other family here would produce an answer about an address the router
        // never reported. Failing is the truthful outcome; a verification is not.
        if (candidates.Count == 0)
            throw new ExternalProbeTransportException(
                $"The external probe service has no {Describe(family)} address, and " +
                $"an {Describe(family)} endpoint is never checked over another address family.");

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(AddressConnectTimeout);
            try
            {
                return await connector.ConnectAsync(candidate, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                // The caller's cancellation and the handler's overall connect budget both end the whole
                // attempt; only this one address's bounded wait moves on to the next address.
                cancellationToken.ThrowIfCancellationRequested();
                last = exception;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                last = exception;
            }
        }

        throw new ExternalProbeTransportException(
            $"The external probe service could not be reached over {Describe(family)}.", last);
    }

    private static string Describe(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => "IPv4",
        AddressFamily.InterNetworkV6 => "IPv6",
        _ => "this address family"
    };
}

/// <summary>
/// Resolves the probe service's hostname. A seam, not a DNS layer: one method, one call, and the only
/// production implementation asks the operating system.
/// </summary>
public interface IExternalProbeAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// The operating system's own resolver. Both families are requested deliberately: the filtering is
/// done afterwards, which is what makes the choice independent of what the resolver lists first.
/// </summary>
public sealed class SystemProbeAddressResolver : IExternalProbeAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

/// <summary>Opens one TCP connection to one already-resolved address.</summary>
public interface IExternalProbeSocketConnector
{
    Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}

/// <summary>The real socket. The family comes from the endpoint, so nothing here can widen it.</summary>
public sealed class TcpProbeSocketConnector : IExternalProbeSocketConnector
{
    public async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A transport failure this assembly described itself. Its message is safe to show as technical
/// detail precisely because it is never a platform or provider exception's own words.
/// </summary>
public sealed class ExternalProbeTransportException : Exception
{
    public ExternalProbeTransportException() { }

    public ExternalProbeTransportException(string message) : base(message) { }

    public ExternalProbeTransportException(string message, Exception? innerException)
        : base(message, innerException) { }
}
