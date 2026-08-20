using System.Globalization;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>
/// Turns the live Vanilla state into the review and result screens.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static: selection, resolved version, the destination the Agent answered with and the
/// acceptance state in, labelled rows out. It cannot reach a provider, so it cannot show anything
/// that was not already established.
/// </para>
/// <para>
/// The editorial rules live here. Labels are nouns, values are states, and nothing is written in the
/// past tense before it has happened: at review time nothing has been downloaded, hashed, installed
/// or registered. Provenance, hashes and URLs belong to the technical rows, which the view collapses.
/// </para>
/// </remarks>
public static class LiveVanillaReviewBuilder
{
    /// <summary>What a Vanilla server is, before any of the detail.</summary>
    public const string Implementation = "Vanilla (official Mojang server)";

    /// <summary>Wording used wherever a value was never established.</summary>
    public const string NotEstablished = "Not established";

    public static CreationReviewSummary Build(
        string serverName,
        VanillaVersionOption? version,
        VanillaDestinationPreview? destination,
        VanillaEulaAcceptance eula,
        VanillaVersionCatalog? catalog,
        IReadOnlyList<string> blockingIssues) =>
        Build(serverName, version, destination, eula, catalog, 4_096,
            ServerPortPolicy.DefaultPort, VanillaNetworkingPreference.FriendsOverInternet, blockingIssues);

    public static CreationReviewSummary Build(
        string serverName,
        VanillaVersionOption? version,
        VanillaDestinationPreview? destination,
        VanillaEulaAcceptance eula,
        VanillaVersionCatalog? catalog,
        int maximumMemoryMib,
        IReadOnlyList<string> blockingIssues) =>
        Build(serverName, version, destination, eula, catalog, maximumMemoryMib,
            ServerPortPolicy.DefaultPort, VanillaNetworkingPreference.FriendsOverInternet, blockingIssues);

    public static CreationReviewSummary Build(
        string serverName,
        VanillaVersionOption? version,
        VanillaDestinationPreview? destination,
        VanillaEulaAcceptance eula,
        VanillaVersionCatalog? catalog,
        int maximumMemoryMib,
        int port,
        VanillaNetworkingPreference networkingPreference,
        IReadOnlyList<string> blockingIssues)
    {
        if (version is null)
            return new CreationReviewSummary { BlockingIssues = blockingIssues };

        var sections = new List<CreationReviewSection>
        {
            new("Server",
            [
                new CreationReviewRow("Name", serverName.Trim(), serverName.Trim().Length == 0, "Not set"),
                new CreationReviewRow("Type", Implementation),
                new CreationReviewRow("Minecraft version", version.VersionId),
                new CreationReviewRow("Release channel", DescribeChannel(version.Channel)),
                new CreationReviewRow("Maximum memory",
                    $"{MemoryAllocationPolicy.FormatGigabytes(maximumMemoryMib, CultureInfo.CurrentCulture)} GB ({maximumMemoryMib} MiB)")
            ]),
            new("Runtime",
            [
                new CreationReviewRow(
                    "Java requirement",
                    version.RequiredJavaMajor is { } major ? $"Java {major}" : "",
                    version.RequiredJavaMajor is null,
                    NotEstablished),
                new CreationReviewRow("Requirement source", DescribeJavaSource(version.JavaRequirementSource)),
                new CreationReviewRow("Runtime plan", ManagedJavaBehaviour(version.RequiredJavaMajor))
            ]),
            new("Location",
            [
                new CreationReviewRow(
                    "Destination", destination?.CanonicalDestination ?? "",
                    string.IsNullOrEmpty(destination?.CanonicalDestination), NotEstablished, true),
                new CreationReviewRow("Folder check", DescribeDestination(destination))
            ]),
            new("Access",
            [
                new CreationReviewRow("Initial state", "Stopped"),
                new CreationReviewRow("Server port", port.ToString(CultureInfo.InvariantCulture)),
                new CreationReviewRow("Networking preference", DescribeNetworkingPreference(networkingPreference)),
                new CreationReviewRow("Public access", "Not configured"),
                new CreationReviewRow("Port availability", "Checked when the server starts"),
                new CreationReviewRow("World", "Created on first start")
            ])
        };

        var evidence = new List<CreationReviewRow>
        {
            new("Artifact source", DescribeArtifact(version)),
            new("Published checksum", version.ServerSha1.Length > 0 ? $"SHA-1 {version.ServerSha1}" : "",
                version.ServerSha1.Length == 0, "None published"),
            new("Artifact size", version.ServerSizeBytes is { } size
                ? string.Create(CultureInfo.CurrentCulture, $"{size:N0} bytes")
                : "", version.ServerSizeBytes is null, "Not published"),
            new("Version details", version.MetadataUrl, version.MetadataUrl.Length == 0, NotEstablished),
            new("Published by", version.Provenance),
            new("Retrieved", catalog?.RetrievedUtc is { } retrieved ? FormatLocal(retrieved) : "",
                catalog?.RetrievedUtc is null, NotEstablished),
            new("Metadata freshness", DescribeFreshness(catalog)),
            new("Folder name", destination?.FolderName ?? "",
                string.IsNullOrEmpty(destination?.FolderName), NotEstablished)
        };

        var notes = new List<CreationReviewNote>
        {
            new("Integrity",
                version.ServerSha1.Length > 0
                    ? "Mojang publishes a SHA-1 for this download. The same hash is computed after "
                      + "downloading and the file is refused if it does not match. That confirms the file is "
                      + "the published one; it is not a signature and not a malware check."
                    : "Mojang published no checksum for this version, so the download cannot be confirmed "
                      + "byte-for-byte. It is still obtained over HTTPS from Mojang.")
        };

        var warnings = version.Warnings.ToList();
        if (catalog?.IsStale == true)
            warnings.Add("This version list is a saved copy. Mojang could not be reached to check it.");
        if (destination is { IsAvailable: false, Message.Length: > 0 })
            warnings.Add(destination.Message);

        return new CreationReviewSummary
        {
            Sections = sections,
            EvidenceRows = evidence,
            EvidenceNotes = notes,
            Warnings = warnings,
            BlockingIssues = blockingIssues,
            PreviewNotice = ""
        };
    }

    public static string DescribeNetworkingPreference(VanillaNetworkingPreference preference) => preference switch
    {
        VanillaNetworkingPreference.FriendsOverInternet => "Friends over the internet — setup still required",
        VanillaNetworkingPreference.ThisNetworkOnly => "This network only — setup still required",
        VanillaNetworkingPreference.ThisComputerOnly => "This computer only — no network access requested",
        VanillaNetworkingPreference.HomeNetwork => "Home network — Windows Firewall may still need approval",
        _ => "Decide later — networking remains unconfigured"
    };

    /// <summary>The one sentence describing the runtime plan, wherever it appears.</summary>
    public static string ManagedJavaBehaviour(int? requiredMajor) => requiredMajor is { } major
        ? $"Managed Java {major}, reused if present, otherwise obtained from Eclipse Adoptium"
        : "Cannot be planned until the requirement is established";

    public static string DescribeChannel(VanillaReleaseChannel channel) => channel switch
    {
        VanillaReleaseChannel.Stable => "Release",
        VanillaReleaseChannel.Snapshot => "Snapshot",
        _ => "Historic"
    };

    public static string DescribeJavaSource(JavaRequirementSource source) => source switch
    {
        JavaRequirementSource.OfficialMetadata => "Official version metadata",
        JavaRequirementSource.ChunkPilotPolicy => "Derived from the version number",
        _ => NotEstablished
    };

    public static string DescribeFreshness(VanillaVersionCatalog? catalog) => catalog switch
    {
        null => NotEstablished,
        { ProviderAvailable: false } => "Unavailable",
        { IsStale: true } => "Stale saved copy",
        { IsFromCache: true } => "Saved copy",
        _ => "Current"
    };

    /// <summary>What the destination check concluded, in one value.</summary>
    public static string DescribeDestination(VanillaDestinationPreview? destination) => destination switch
    {
        null => "Pending",
        { IsAvailable: true } => "Available",
        _ => "Blocked"
    };

    /// <summary>Names the host the artifact comes from without implying it has been fetched.</summary>
    public static string DescribeArtifact(VanillaVersionOption version)
    {
        if (!version.HasServerDownload || version.ServerDownloadUrl.Length == 0)
            return "No server download published";
        return Uri.TryCreate(version.ServerDownloadUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Host} over {uri.Scheme.ToUpperInvariant()}"
            : "Mojang";
    }

    /// <summary>
    /// The one secondary line under a version in the list.
    /// </summary>
    /// <remarks>
    /// Composed from the parts that exist rather than from a fixed template, so a version with no
    /// published release date or no established Java requirement reads as a shorter sentence instead
    /// of a line with a gap and a stray separator in it.
    /// </remarks>
    public static string DescribeVersionLine(VanillaVersionOption version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = new List<string>();
        if (version.ReleaseTime is { } released)
            parts.Add((version.Channel == VanillaReleaseChannel.Snapshot ? "Published " : "Released ")
                      + released.ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.CurrentCulture));
        parts.Add(version.RequiredJavaMajor is { } major
            ? $"Requires Java {major}"
            : "Java requirement unknown");
        if (version.Channel == VanillaReleaseChannel.Snapshot)
            parts.Add("Snapshot");
        return string.Join(" · ", parts);
    }

    /// <summary>Local date and 12-hour time, matching the rest of the interface.</summary>
    public static string FormatLocal(DateTimeOffset? moment) => moment is { } value
        ? value.ToLocalTime().ToString("MMMM d, yyyy, h:mm tt", CultureInfo.CurrentCulture)
        : NotEstablished;
}
