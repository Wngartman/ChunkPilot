using System.Net;
using System.Net.Sockets;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Streams Modrinth pack members without automatic redirects. Every hop is HTTPS, host-checked,
/// and resolved away from loopback, private, link-local, documentation, and other non-public ranges.
/// Hash and size verification remain the materializer's independent trust boundary.
/// </summary>
public sealed class ModrinthPackHttpDownloadSource : IModrinthPackDownloadSource, IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient http;
    private readonly ModrinthPackReader reader;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveAddresses;
    private bool disposed;

    public ModrinthPackHttpDownloadSource()
        : this(CreateHandler(), static (host, token) => Dns.GetHostAddressesAsync(host, token), disposeHandler: true)
    {
    }

    internal ModrinthPackHttpDownloadSource(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAddresses,
        bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(resolveAddresses);
        http = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3");
        reader = new ModrinthPackReader();
        this.resolveAddresses = resolveAddresses;
    }

    public async Task<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(uri);
        var origin = reader.ValidateDownloadUrl(uri.AbsoluteUri).Origin;
        var current = uri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateHop(current, origin);
            await EnsurePublicResolutionAsync(current.IdnHost, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                try
                {
                    response.EnsureSuccessStatusCode();
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return new ResponseOwnedStream(stream, response);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException("A Modrinth pack download redirect did not include a Location header.");
            if (redirect == MaximumRedirects)
                throw new HttpRequestException($"A Modrinth pack download exceeded {MaximumRedirects} redirects.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new HttpRequestException($"A Modrinth pack download exceeded {MaximumRedirects} redirects.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        http.Dispose();
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false,
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new InvalidDataException(
                $"Modrinth pack download host {context.DnsEndPoint.Host} resolved to a non-public address.");

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
                socket.Dispose();
            }
        }
        throw new HttpRequestException(
            $"Could not connect to trusted Modrinth download host {context.DnsEndPoint.Host}.", lastFailure);
    }

    private static void ValidateHop(Uri uri, ModrinthPackDownloadOrigin originalOrigin)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Modrinth pack redirects require absolute HTTPS URLs without credentials, custom ports, or fragments.");

        var host = uri.IdnHost.ToLowerInvariant();
        var allowed = originalOrigin switch
        {
            ModrinthPackDownloadOrigin.ModrinthCdn => host == "cdn.modrinth.com",
            ModrinthPackDownloadOrigin.GitHub or ModrinthPackDownloadOrigin.GitHubRaw =>
                host is "github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com" or
                    "release-assets.githubusercontent.com" or "github-releases.githubusercontent.com",
            ModrinthPackDownloadOrigin.GitLab => host == "gitlab.com" || host == "storage.googleapis.com" ||
                                                 host.EndsWith(".storage.googleapis.com", StringComparison.Ordinal),
            _ => false
        };
        if (!allowed)
            throw new InvalidDataException($"Modrinth pack download redirected to an untrusted host: {uri.IdnHost}.");
    }

    private async Task EnsurePublicResolutionAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolveAddresses(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException($"Could not resolve trusted Modrinth download host {host}.", exception);
        }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new InvalidDataException($"Modrinth pack download host {host} resolved to a non-public address.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                >= 224 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                _ => true
            };
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        if ((bytes[0] & 0xFE) == 0xFC) // fc00::/7 unique-local
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) // documentation range
            return false;
        return (bytes[0] & 0xE0) == 0x20; // globally routable 2000::/3
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        private bool streamDisposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !streamDisposed)
            {
                streamDisposed = true;
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!streamDisposed)
            {
                streamDisposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
            }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
