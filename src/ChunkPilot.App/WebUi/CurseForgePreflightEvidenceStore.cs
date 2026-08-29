using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.App.WebUi;

/// <summary>
/// Keeps the native CurseForge review bound to the exact WebUI selection that requested it.
/// The Agent remains authoritative and consumes the operation identity once; this store prevents
/// a stale browser review from being attached to a different creation request before it reaches
/// that boundary.
/// </summary>
internal sealed class CurseForgePreflightEvidenceStore
{
    internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    private readonly object sync = new();
    private readonly TimeSpan lifetime;
    private long generation;
    private CurseForgePreflightEvidence? current;

    public CurseForgePreflightEvidenceStore(TimeSpan? lifetime = null)
    {
        this.lifetime = lifetime ?? DefaultLifetime;
        if (this.lifetime <= TimeSpan.Zero || this.lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "CurseForge WebUI preflight evidence must expire within one hour.");
    }

    public long Invalidate()
        => InvalidateWithEvidence().Generation;

    public CurseForgePreflightInvalidation InvalidateWithEvidence()
    {
        lock (sync)
        {
            var operationId = current?.OperationId;
            current = null;
            return new CurseForgePreflightInvalidation(++generation, operationId);
        }
    }

    public bool InvalidateIfOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
            return false;
        lock (sync)
        {
            if (current?.OperationId != operationId)
                return false;
            current = null;
            ++generation;
            return true;
        }
    }

    public bool IsCurrent(long candidateGeneration)
    {
        lock (sync)
            return generation == candidateGeneration;
    }

    public bool TryCommit(
        long candidateGeneration,
        Guid reviewId,
        string selectionMethod,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult result,
        DateTimeOffset now,
        out CurseForgePreflightEvidence evidence)
    {
        evidence = CurseForgePreflightEvidence.From(
            reviewId, selectionMethod, project, release, result, now + lifetime);
        lock (sync)
        {
            if (generation != candidateGeneration)
                return false;
            current = evidence;
            return true;
        }
    }

    public CurseForgePreflightEvidence Require(
        Guid reviewId,
        string selectionMethod,
        string projectId,
        string clientFileId,
        DateTimeOffset now)
    {
        lock (sync)
        {
            if (current is not { } candidate)
                throw StaleReview();
            if (!candidate.MatchesSelection(
                    reviewId, selectionMethod, projectId, clientFileId))
                throw StaleReview();
            if (candidate.IsCurrent(now))
                return candidate;

            current = null;
            ++generation;
            throw StaleReview(candidate.OperationId);
        }
    }

    public CurseForgePreflightEvidence Consume(
        Guid reviewId,
        string selectionMethod,
        string projectId,
        string clientFileId,
        DateTimeOffset now)
    {
        lock (sync)
        {
            if (current is not { } candidate)
                throw StaleReview();
            if (!candidate.MatchesSelection(
                    reviewId, selectionMethod, projectId, clientFileId))
                throw StaleReview();

            current = null;
            ++generation;
            if (!candidate.IsCurrent(now))
                throw StaleReview(candidate.OperationId);

            return candidate;
        }
    }

    private static StaleCurseForgePreflightException StaleReview(Guid? operationId = null) => new(operationId);
}

internal sealed record CurseForgePreflightInvalidation(long Generation, Guid? OperationId);

internal sealed class StaleCurseForgePreflightException(Guid? operationId) : InvalidOperationException(
    "The CurseForge release review is stale or belongs to another selection. Inspect the exact release again.")
{
    public Guid? OperationId { get; } = operationId;
}

/// <summary>
/// Pre-arms exact Agent revocation before a preflight request is sent. This lets cancellation,
/// disconnect and response-deserialization failure revoke a late authorization through the Agent's
/// pre-completion tombstone even when no Ready response reached the App.
/// </summary>
internal sealed class CurseForgePreflightRevocationLease(
    Guid operationId,
    Func<Guid, Task> revokeAsync) : IAsyncDisposable
{
    private int completed;

    public Guid OperationId { get; } = operationId != Guid.Empty
        ? operationId
        : throw new ArgumentException("CurseForge preflight revocation requires an exact operation identity.",
            nameof(operationId));

    public void Complete() => Interlocked.Exchange(ref completed, 1);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            await revokeAsync(OperationId).ConfigureAwait(false);
    }
}

internal sealed record CurseForgePreflightEvidence
{
    public required Guid ReviewId { get; init; }
    public required string SelectionMethod { get; init; }
    public required Guid OperationId { get; init; }
    public required ModpackCreationSource SourceKind { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectSlug { get; init; }
    public required string ProjectName { get; init; }
    public required string ClientFileId { get; init; }
    public required string ServerPackFileId { get; init; }
    public required string VersionName { get; init; }
    public required ReleaseChannel ReleaseChannel { get; init; }
    public required string ClientDownloadUrl { get; init; }
    public required string ClientSha1 { get; init; }
    public required string ClientSha256 { get; init; }
    public required long ClientSizeBytes { get; init; }
    public required string ServerPackDownloadUrl { get; init; }
    public required string ServerPackSha1 { get; init; }
    public required long? ServerPackSizeBytes { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string Loader { get; init; }
    public required string LoaderVersion { get; init; }
    public required int RequiredJavaMajor { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public static CurseForgePreflightEvidence From(
        Guid reviewId,
        string selectionMethod,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult result,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(result);
        if (reviewId == Guid.Empty)
            throw new ArgumentException("The CurseForge selection review identity is invalid.", nameof(reviewId));
        RequireSelectionMethod(selectionMethod);
        if (project.Provider != CatalogProvider.CurseForge ||
            !project.ProjectId.Equals(result.ProjectId, StringComparison.Ordinal) ||
            !release.VersionId.Equals(result.ClientFileId, StringComparison.Ordinal) ||
            !release.ClientFileId.Equals(result.ClientFileId, StringComparison.Ordinal) ||
            !release.ServerPackFileId.Equals(result.ServerPackFileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "CurseForge preflight returned evidence for a different project or release.");
        }

        if (result.State != CatalogReleasePreflightState.Ready || result.OperationId == Guid.Empty ||
            !ApprovedUrl(result.ClientDownloadUrl) || !Sha1(result.ClientSha1) ||
            !Sha256(result.ClientSha256) || result.ClientSizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(result.MinecraftVersion) || string.IsNullOrWhiteSpace(result.Loader) ||
            string.IsNullOrWhiteSpace(result.LoaderVersion) || result.RequiredJavaMajor <= 0)
        {
            throw new InvalidDataException(
                "A complete successful CurseForge preflight is required before creation can be reviewed.");
        }

        var official = result.ServerPackFileId.Length > 0;
        if (official)
        {
            if (!release.HasServerPackage || !ApprovedUrl(result.ServerPackDownloadUrl) ||
                !Sha1(result.ServerPackSha1) || result.ServerPackSizeBytes is not > 0)
            {
                throw new InvalidDataException(
                    "The successful CurseForge preflight lacks exact official server-pack evidence.");
            }
        }
        else if (release.HasServerPackage || !release.CanGenerateServerCandidate ||
                 result.ServerPackDownloadUrl.Length > 0 || result.ServerPackSha1.Length > 0 ||
                 result.ServerPackSizeBytes is not null)
        {
            throw new InvalidDataException(
                "The successful CurseForge preflight contains contradictory generated-candidate evidence.");
        }

        return new CurseForgePreflightEvidence
        {
            ReviewId = reviewId,
            SelectionMethod = selectionMethod,
            OperationId = result.OperationId,
            SourceKind = official
                ? ModpackCreationSource.CurseForgeOfficialServerPack
                : ModpackCreationSource.CurseForgeGeneratedCandidate,
            ProjectId = result.ProjectId,
            ProjectSlug = project.Slug,
            ProjectName = project.Name,
            ClientFileId = result.ClientFileId,
            ServerPackFileId = result.ServerPackFileId,
            VersionName = release.VersionName,
            ReleaseChannel = release.ReleaseChannel,
            ClientDownloadUrl = result.ClientDownloadUrl,
            ClientSha1 = result.ClientSha1,
            ClientSha256 = result.ClientSha256,
            ClientSizeBytes = result.ClientSizeBytes,
            ServerPackDownloadUrl = result.ServerPackDownloadUrl,
            ServerPackSha1 = result.ServerPackSha1,
            ServerPackSizeBytes = result.ServerPackSizeBytes,
            MinecraftVersion = result.MinecraftVersion,
            Loader = result.Loader,
            LoaderVersion = result.LoaderVersion,
            RequiredJavaMajor = result.RequiredJavaMajor,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public CatalogVersion ApplyTo(CatalogVersion release) => release with
    {
        DownloadUrl = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackDownloadUrl : release.DownloadUrl,
        Sha1 = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackSha1 : release.Sha1,
        SizeBytes = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackSizeBytes : release.SizeBytes,
        MinecraftVersion = MinecraftVersion,
        Loader = Loader,
        LoaderVersion = LoaderVersion,
        RequiredJavaMajor = RequiredJavaMajor,
        ClientDownloadUrl = ClientDownloadUrl,
        ClientSha1 = ClientSha1,
        ClientSha256 = ClientSha256,
        ClientSizeBytes = ClientSizeBytes,
        CreationPreflightState = CatalogReleasePreflightState.Ready
    };

    public ModpackCreationPlan Bind(ModpackCreationPlan plan) => plan with
    {
        PreflightOperationId = OperationId,
        SourceKind = SourceKind,
        Source = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackDownloadUrl : ClientDownloadUrl,
        Provider = UpdateProvider.CurseForge,
        ProjectId = ProjectId,
        ProjectSlug = ProjectSlug,
        ProjectName = ProjectName,
        VersionId = ClientFileId,
        ServerPackFileId = ServerPackFileId,
        VersionName = VersionName,
        ReleaseChannel = ReleaseChannel,
        MinecraftVersion = MinecraftVersion,
        Loader = Loader,
        LoaderVersion = LoaderVersion,
        RequiredJavaMajor = RequiredJavaMajor,
        ExpectedSha1 = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackSha1 : ClientSha1,
        ExpectedSha256 = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? "" : ClientSha256,
        ExpectedSha512 = "",
        ExpectedSizeBytes = SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
            ? ServerPackSizeBytes : ClientSizeBytes,
        VerifiedClientArchiveSha256 = ClientSha256
    };

    public bool MatchesSelection(
        Guid reviewId,
        string selectionMethod,
        string projectId,
        string clientFileId) =>
        reviewId == ReviewId &&
        SelectionMethod.Equals(selectionMethod, StringComparison.Ordinal) &&
        projectId.Equals(ProjectId, StringComparison.Ordinal) &&
        clientFileId.Equals(ClientFileId, StringComparison.Ordinal);

    public bool IsCurrent(DateTimeOffset now) => now < ExpiresAtUtc;

    public static string RequireSelectionMethod(string value) => value switch
    {
        "Browse" or "Link" => value,
        _ => throw new ArgumentException("The remote modpack selection method is invalid.", nameof(value))
    };

    private static bool ApprovedUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        CurseForgeApiClient.IsApprovedDownloadUri(uri);

    private static bool Sha1(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
