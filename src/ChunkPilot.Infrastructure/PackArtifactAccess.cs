using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public enum PackArtifactAccessState
{
    Available,
    CredentialRequired,
    DistributionUnavailable
}

/// <summary>
/// Describes whether provider metadata authorizes an artifact download. Provider credentials remain
/// outside this value so they cannot be serialized into URLs, logs, or persisted pack metadata.
/// </summary>
public sealed record PackArtifactAccess
{
    private PackArtifactAccess(
        UpdateProvider provider,
        PackArtifactAccessState state,
        Uri? downloadUri,
        string unavailableReason)
    {
        Provider = provider;
        State = state;
        DownloadUri = downloadUri;
        UnavailableReason = unavailableReason;
    }

    public UpdateProvider Provider { get; }
    public PackArtifactAccessState State { get; }
    public Uri? DownloadUri { get; }
    public string UnavailableReason { get; }
    public bool IsAvailable => State == PackArtifactAccessState.Available;

    public static PackArtifactAccess Available(UpdateProvider provider, Uri downloadUri)
    {
        ArgumentNullException.ThrowIfNull(downloadUri);
        if (!downloadUri.IsAbsoluteUri || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Pack artifacts require an absolute HTTPS URL.", nameof(downloadUri));
        return new PackArtifactAccess(provider, PackArtifactAccessState.Available, downloadUri, "");
    }

    public static PackArtifactAccess CredentialRequired(UpdateProvider provider, string reason) =>
        Unavailable(provider, PackArtifactAccessState.CredentialRequired, reason);

    public static PackArtifactAccess DistributionUnavailable(UpdateProvider provider, string reason) =>
        Unavailable(provider, PackArtifactAccessState.DistributionUnavailable, reason);

    private static PackArtifactAccess Unavailable(
        UpdateProvider provider,
        PackArtifactAccessState state,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An unavailable artifact requires a truthful reason.", nameof(reason));
        return new PackArtifactAccess(provider, state, null, reason.Trim());
    }
}
