using System.Globalization;
using System.Net;
using System.Xml.Linq;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// UPnP Internet Gateway Device port mapping, per the UPnP Forum WANIPConnection:1 service template and
/// the SSDP discovery and SOAP control rules of the UPnP Device Architecture.
/// </summary>
/// <remarks>
/// <para>
/// Implemented: SSDP M-SEARCH to 239.255.255.250:1900 for WANIPConnection and WANPPPConnection service
/// types, retrieval of the device description from the LOCATION header, relative URL resolution against
/// URLBase or the description URL, and the SOAP actions GetExternalIPAddress,
/// GetSpecificPortMappingEntry, AddPortMapping and DeletePortMapping.
/// </para>
/// <para>
/// Only responses whose source address is the selected gateway are accepted. Any other LAN device may
/// answer an SSDP search, and treating one as the router would forward a port through the wrong device.
/// </para>
/// <para>
/// RemoteHost is always the empty-string wildcard and NewEnabled is always 1. External and internal
/// port are always equal, which also avoids error 724 (SamePortValuesRequired) on gateways that require
/// it. Error 725 (OnlyPermanentLeasesSupported) is retried once with a lease duration of 0 and the
/// resulting mapping is reported as permanent, because a mapping the router will never expire has
/// materially different cleanup consequences.
/// </para>
/// <para>
/// Not implemented: eventing (GENA), the IGD:2 actions AddAnyPortMapping, DeletePortMappingRange and
/// GetListOfPortMappings, GetGenericPortMappingEntry enumeration, IPv6 firewall control, and M-POST
/// fallback for control.
/// </para>
/// </remarks>
public sealed class UpnpIgdMappingProvider : IRouterMappingProvider
{
    /// <summary>Preference order: IGD:2 first, then IGD:1, then the PPP variant.</summary>
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    ];

    private static readonly XNamespace DeviceNamespace = "urn:schemas-upnp-org:device-1-0";

    private readonly ISsdpSearchChannel ssdp;
    private readonly IUpnpControlChannel control;
    private readonly RouterMappingOptions options;

    public UpnpIgdMappingProvider(
        ISsdpSearchChannel ssdp, IUpnpControlChannel control, RouterMappingOptions options)
    {
        this.ssdp = ssdp;
        this.control = control;
        this.options = options;
    }

    public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;

    public bool CanQueryExistingMappings => true;

    public async Task<RouterDiscoveryResult> DiscoverAsync(
        RouterLanBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        IReadOnlyList<SsdpDiscoveryResponse> responses;
        try
        {
            responses = await ssdp
                .SearchAsync(binding.LocalAddress, SearchTargets, options.SsdpMaximumWait, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException)
        {
            return Unsupported(RouterMappingFailure.NetworkFailure,
                $"The SSDP search could not be sent: {exception.Message}");
        }

        // The router is the only device whose answer may be trusted, and duplicate answers to the same
        // search are normal, so responses are filtered to the gateway and reduced to one per location.
        var fromGateway = responses
            .Where(response => response.Source.Equals(binding.GatewayAddress))
            .Where(response => Uri.TryCreate(response.Location, UriKind.Absolute, out _))
            .GroupBy(response => response.Location, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(response => TargetRank(response.SearchTarget))
            .ToArray();
        if (fromGateway.Length == 0)
            return Unsupported(
                responses.Count == 0
                    ? RouterMappingFailure.GatewayDidNotRespond
                    : RouterMappingFailure.MechanismUnsupported,
                responses.Count == 0
                    ? "No device answered the SSDP search for an Internet Gateway Device."
                    : $"{responses.Count} SSDP answer(s) arrived, but none came from the gateway {binding.GatewayAddress}.");

        foreach (var response in fromGateway)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = await ReadDescriptionAsync(new Uri(response.Location), cancellationToken)
                .ConfigureAwait(false);
            if (description is null)
                continue;
            var service = FindConnectionService(description.Value.Document, description.Value.BaseUri);
            if (service is null)
                continue;

            var external = await control.InvokeAsync(
                    service.ControlUrl, service.ServiceType, "GetExternalIPAddress", [], cancellationToken)
                .ConfigureAwait(false);
            var externalAddress = external.Success &&
                                  external.Values.TryGetValue("NewExternalIPAddress", out var value)
                ? value.Trim()
                : "";
            return new RouterDiscoveryResult
            {
                Mechanism = Mechanism,
                Supported = true,
                ExternalAddress = externalAddress,
                ControlUrl = service.ControlUrl.ToString(),
                ServiceType = service.ServiceType,
                Detail = $"UPnP {service.ServiceType} answered at {service.ControlUrl}" +
                         (externalAddress.Length > 0
                             ? $" and reported external address {externalAddress}."
                             : external.Success
                                 ? " but reported no external address."
                                 : $" and refused GetExternalIPAddress with UPnP error {external.ErrorCode}.")
            };
        }
        return Unsupported(RouterMappingFailure.MechanismUnsupported,
            "The gateway answered SSDP but published no readable WAN connection service.");
    }

    public async Task<ExistingRouterMapping?> QueryAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        MappingTransport transport,
        int externalPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!Uri.TryCreate(discovery.ControlUrl, UriKind.Absolute, out var controlUrl))
            return null;
        var response = await control.InvokeAsync(controlUrl, discovery.ServiceType,
            "GetSpecificPortMappingEntry",
            [
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewProtocol", ProtocolName(transport))
            ], cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            return null; // 714 NoSuchEntryInArray, or any refusal: nothing is proven to exist.

        return new ExistingRouterMapping
        {
            ExternalPort = externalPort,
            Transport = transport,
            InternalClient = Value(response, "NewInternalClient"),
            InternalPort = ParseInt(Value(response, "NewInternalPort")),
            Description = Value(response, "NewPortMappingDescription"),
            Enabled = Value(response, "NewEnabled") != "0",
            LeaseSeconds = ParseInt(Value(response, "NewLeaseDuration"))
        };
    }

    public async Task<RouterMappingOutcome> CreateAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(discovery.ControlUrl, UriKind.Absolute, out var controlUrl))
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MechanismUnsupported,
                "No UPnP control endpoint is known for this gateway.");

        var attempt = await AddAsync(controlUrl, discovery.ServiceType, binding, request,
            request.LeaseSeconds, cancellationToken).ConfigureAwait(false);
        if (attempt.Success)
            return new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                LeaseSeconds = request.LeaseSeconds,
                LeaseIsFinite = true,
                ExternalAddress = discovery.ExternalAddress,
                ControlUrl = discovery.ControlUrl,
                ServiceType = discovery.ServiceType,
                Detail = $"UPnP AddPortMapping accepted {ProtocolName(request.Transport)} {request.ExternalPort} " +
                         $"to {binding.LocalAddress}:{request.InternalPort} for {request.LeaseSeconds} seconds."
            };

        if (attempt.ErrorCode == 725)
        {
            // OnlyPermanentLeasesSupported. A permanent mapping is a materially different state: the
            // router will not expire it, so cleanup is entirely ChunkPilot's responsibility.
            var permanent = await AddAsync(controlUrl, discovery.ServiceType, binding, request, 0, cancellationToken)
                .ConfigureAwait(false);
            if (permanent.Success)
                return new RouterMappingOutcome
                {
                    Success = true,
                    Mechanism = Mechanism,
                    ExternalPort = request.ExternalPort,
                    LeaseSeconds = 0,
                    LeaseIsFinite = false,
                    ExternalAddress = discovery.ExternalAddress,
                    ControlUrl = discovery.ControlUrl,
                    ServiceType = discovery.ServiceType,
                    Detail = $"UPnP AddPortMapping accepted {ProtocolName(request.Transport)} {request.ExternalPort} " +
                             $"to {binding.LocalAddress}:{request.InternalPort} as a permanent mapping; the router " +
                             "reported error 725 (OnlyPermanentLeasesSupported) for a timed lease."
                };
            return Translate(permanent, request);
        }
        return Translate(attempt, request);
    }

    public async Task<RouterMappingOutcome> RemoveAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(discovery.ControlUrl, UriKind.Absolute, out var controlUrl))
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RemovalFailed,
                "No UPnP control endpoint is known for this gateway, so the mapping could not be removed.");

        var response = await control.InvokeAsync(controlUrl, discovery.ServiceType, "DeletePortMapping",
            [
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", request.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewProtocol", ProtocolName(request.Transport))
            ], cancellationToken).ConfigureAwait(false);

        // 714 NoSuchEntryInArray means the entry is already gone, which is the state removal wanted.
        if (response.Success || response.ErrorCode == 714)
            return new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                Detail = response.Success
                    ? $"UPnP DeletePortMapping removed {ProtocolName(request.Transport)} {request.ExternalPort}."
                    : $"UPnP reported error 714 (NoSuchEntryInArray) for {ProtocolName(request.Transport)} " +
                      $"{request.ExternalPort}; the mapping was already absent."
            };
        return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RemovalFailed,
            $"UPnP DeletePortMapping failed with error {response.ErrorCode} ({UpnpErrorName(response.ErrorCode)}).");
    }

    private async Task<UpnpSoapResponse> AddAsync(
        Uri controlUrl,
        string serviceType,
        RouterLanBinding binding,
        RouterMappingRequest request,
        int leaseSeconds,
        CancellationToken cancellationToken) =>
        await control.InvokeAsync(controlUrl, serviceType, "AddPortMapping",
            [
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", request.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewProtocol", ProtocolName(request.Transport)),
                new KeyValuePair<string, string>("NewInternalPort", request.InternalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewInternalClient", binding.LocalAddress.ToString()),
                new KeyValuePair<string, string>("NewEnabled", "1"),
                new KeyValuePair<string, string>("NewPortMappingDescription", request.Description),
                new KeyValuePair<string, string>("NewLeaseDuration", leaseSeconds.ToString(CultureInfo.InvariantCulture))
            ], cancellationToken).ConfigureAwait(false);

    private RouterMappingOutcome Translate(UpnpSoapResponse response, RouterMappingRequest request)
    {
        var failure = response.ErrorCode switch
        {
            718 => RouterMappingFailure.ForeignMappingPresent,
            401 or 606 => RouterMappingFailure.NotAuthorized,
            402 or 715 or 716 or 724 or 727 => RouterMappingFailure.RequestRejected,
            501 => RouterMappingFailure.RequestRejected,
            0 => RouterMappingFailure.NetworkFailure,
            _ => RouterMappingFailure.Unknown
        };
        var detail = response.ErrorCode == 0
            ? $"The gateway did not complete AddPortMapping: {response.ErrorDescription}"
            : $"UPnP AddPortMapping failed for {ProtocolName(request.Transport)} {request.ExternalPort} with " +
              $"error {response.ErrorCode} ({UpnpErrorName(response.ErrorCode)}).";
        return RouterMappingOutcome.Failed(Mechanism, failure, detail);
    }

    private async Task<(XDocument Document, Uri BaseUri)?> ReadDescriptionAsync(
        Uri location, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await control.GetDescriptionAsync(location, cancellationToken).ConfigureAwait(false);
            return (XDocument.Parse(xml), location);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ConnectionService? FindConnectionService(XDocument document, Uri descriptionUrl)
    {
        // URLBase is deprecated but still emitted; when absent the description URL is the base, which
        // the UPnP Device Architecture states is the preferred behaviour.
        var declaredBase = document.Root?.Element(DeviceNamespace + "URLBase")?.Value?.Trim();
        var baseUri = !string.IsNullOrEmpty(declaredBase) &&
                      Uri.TryCreate(declaredBase, UriKind.Absolute, out var parsedBase)
            ? parsedBase
            : descriptionUrl;

        foreach (var target in SearchTargets)
        {
            foreach (var service in document.Descendants(DeviceNamespace + "service"))
            {
                var serviceType = service.Element(DeviceNamespace + "serviceType")?.Value?.Trim() ?? "";
                if (!string.Equals(serviceType, target, StringComparison.OrdinalIgnoreCase))
                    continue;
                var controlPath = service.Element(DeviceNamespace + "controlURL")?.Value?.Trim() ?? "";
                if (controlPath.Length == 0 || !Uri.TryCreate(baseUri, controlPath, out var controlUrl))
                    continue;
                return new ConnectionService(serviceType, controlUrl);
            }
        }
        return null;
    }

    private static int TargetRank(string searchTarget)
    {
        var index = Array.FindIndex(SearchTargets,
            target => string.Equals(target, searchTarget, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? SearchTargets.Length : index;
    }

    private static string ProtocolName(MappingTransport transport) =>
        transport == MappingTransport.Tcp ? "TCP" : "UDP";

    private static string Value(UpnpSoapResponse response, string name) =>
        response.Values.TryGetValue(name, out var value) ? value.Trim() : "";

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    internal static string UpnpErrorName(int errorCode) => errorCode switch
    {
        401 => "InvalidAction",
        402 => "InvalidArgs",
        404 => "InvalidVar",
        501 => "ActionFailed",
        606 => "ActionNotAuthorized",
        713 => "SpecifiedArrayIndexInvalid",
        714 => "NoSuchEntryInArray",
        715 => "WildCardNotPermittedInSrcIP",
        716 => "WildCardNotPermittedInExtPort",
        718 => "ConflictInMappingEntry",
        724 => "SamePortValuesRequired",
        725 => "OnlyPermanentLeasesSupported",
        726 => "RemoteHostOnlySupportsWildcard",
        727 => "ExternalPortOnlySupportsWildcard",
        _ => "Unassigned"
    };

    private RouterDiscoveryResult Unsupported(RouterMappingFailure failure, string detail) =>
        new() { Mechanism = Mechanism, Supported = false, Failure = failure, Detail = detail };

    private sealed record ConnectionService(string ServiceType, Uri ControlUrl);
}
