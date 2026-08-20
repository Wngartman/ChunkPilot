using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Where the optional external probe service lives, and the rules that decide whether an endpoint is
/// allowed to be used at all.
/// </summary>
/// <remarks>
/// <para>
/// There is no production endpoint yet, and none is compiled in. Until an official service is
/// deployed and configured, <see cref="Configure"/> resolves to "not configured" and the feature
/// truthfully reports that external verification is unavailable in this build.
/// </para>
/// <para>
/// The value is an origin, never a full URL: the client appends
/// <see cref="ExternalReachabilityPolicy.ProbePath"/> itself. A configured value carrying a path,
/// query, fragment, credentials or a non-HTTPS scheme is refused rather than sanitised, so a
/// malformed or hostile setting fails closed instead of redirecting a probe somewhere else.
/// </para>
/// </remarks>
public sealed record ExternalReachabilityProbeOptions
{
    /// <summary>The development configuration name. There is no user-editable production setting.</summary>
    public const string EnvironmentVariable = "CHUNKPILOT_REACHABILITY_PROBE_URL";

    /// <summary>The validated probe URL, or null when this build has no usable endpoint.</summary>
    public Uri? ProbeUrl { get; init; }

    /// <summary>Why an endpoint was refused, or how one was accepted. Technical details only.</summary>
    public string Detail { get; init; } = "No external probe endpoint is configured in this build.";

    /// <summary>
    /// The whole HTTPS exchange, bounded. Comfortably longer than the service's own connect budget so
    /// a genuine unreachable answer arrives rather than being cut off as a client timeout.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The largest response the client will read. A probe answer is a few hundred bytes; anything
    /// larger is refused rather than buffered.
    /// </summary>
    public int MaximumResponseBytes { get; init; } = 4096;

    public bool IsConfigured => ProbeUrl is not null;

    /// <summary>
    /// Reads the development configuration. <paramref name="allowLoopbackHttp"/> exists only for
    /// isolated in-process tests, which serve the fake probe on <c>http://127.0.0.1</c>; production
    /// never passes it, so production is HTTPS-only.
    /// </summary>
    public static ExternalReachabilityProbeOptions FromEnvironment(bool allowLoopbackHttp = false) =>
        Configure(Environment.GetEnvironmentVariable(EnvironmentVariable), allowLoopbackHttp);

    /// <summary>Validates one configured origin. Every rejection is explicit and fails closed.</summary>
    public static ExternalReachabilityProbeOptions Configure(string? origin, bool allowLoopbackHttp = false)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return new ExternalReachabilityProbeOptions();

        var text = origin.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
            return Refuse("The configured external probe endpoint is not an absolute URL.");
        if (parsed.UserInfo.Length > 0)
            return Refuse("The configured external probe endpoint carries credentials, which are never used.");
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            return Refuse("The configured external probe endpoint carries a query or fragment.");
        if (parsed.AbsolutePath is not ("" or "/"))
            return Refuse("The configured external probe endpoint must be an origin without a path.");
        if (parsed.HostNameType == UriHostNameType.Unknown || parsed.Host.Length == 0)
            return Refuse("The configured external probe endpoint has no usable host.");

        var https = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var loopbackHttp = allowLoopbackHttp &&
                           string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
                           parsed.IsLoopback;
        if (!https && !loopbackHttp)
            return Refuse("The configured external probe endpoint is not HTTPS.");

        var probe = new Uri(parsed, ExternalReachabilityPolicy.ProbePath);
        return new ExternalReachabilityProbeOptions
        {
            ProbeUrl = probe,
            Detail = $"External probe endpoint configured: {probe.Scheme}://{probe.Authority}{ExternalReachabilityPolicy.ProbePath}."
        };
    }

    private static ExternalReachabilityProbeOptions Refuse(string detail) =>
        new() { ProbeUrl = null, Detail = detail };
}
