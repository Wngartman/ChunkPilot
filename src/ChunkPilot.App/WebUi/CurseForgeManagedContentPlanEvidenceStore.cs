using ChunkPilot.Core;

namespace ChunkPilot.App.WebUi;

/// <summary>
/// Keeps at most one renderer-visible CurseForge dependency-plan authorization. The Agent owns
/// the trusted plan and consumes it once; this App-side fence makes replacement, cancellation and
/// server switching surface the exact old identity for Agent revocation without exposing plan data.
/// </summary>
internal sealed class CurseForgeManagedContentPlanEvidenceStore
{
    private readonly object sync = new();
    private long generation;
    private CurseForgeManagedContentPlanEvidence? current;

    public ManagedContentPlanInvalidation InvalidateWithEvidence()
    {
        lock (sync)
        {
            var authorizationId = current?.AuthorizationId;
            current = null;
            return new ManagedContentPlanInvalidation(++generation, authorizationId);
        }
    }

    public ManagedContentPlanInvalidation InvalidateForServerChange(Guid? selectedServerId)
    {
        lock (sync)
        {
            if (current is null || current.ServerId == selectedServerId)
                return new ManagedContentPlanInvalidation(generation, null);
            var authorizationId = current.AuthorizationId;
            current = null;
            return new ManagedContentPlanInvalidation(++generation, authorizationId);
        }
    }

    public Guid? InvalidateIfSelection(
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId)
    {
        lock (sync)
        {
            if (current is not { } candidate ||
                !candidate.MatchesSelection(serverId, provider, projectId, versionId))
                return null;
            current = null;
            ++generation;
            return candidate.AuthorizationId;
        }
    }

    public bool InvalidateIfAuthorizationId(Guid authorizationId)
    {
        if (authorizationId == Guid.Empty)
            return false;
        lock (sync)
        {
            if (current?.AuthorizationId != authorizationId)
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
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId,
        ManagedContentPlanAuthorization authorization,
        out CurseForgeManagedContentPlanEvidence evidence)
    {
        evidence = CurseForgeManagedContentPlanEvidence.From(
            serverId, provider, projectId, versionId, authorization);
        lock (sync)
        {
            if (generation != candidateGeneration)
                return false;
            current = evidence;
            return true;
        }
    }

    public CurseForgeManagedContentPlanEvidence Consume(
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId,
        ManagedContentPlanAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (sync)
        {
            if (current is not { } candidate ||
                !candidate.Matches(serverId, provider, projectId, versionId, authorization))
                throw new StaleCurseForgeManagedContentPlanException();
            current = null;
            ++generation;
            return candidate;
        }
    }
}

internal sealed record ManagedContentPlanInvalidation(long Generation, Guid? AuthorizationId);

internal sealed class StaleCurseForgeManagedContentPlanException : InvalidOperationException
{
    public StaleCurseForgeManagedContentPlanException() : base(
        "The CurseForge dependency-plan review is stale or belongs to another selection. Review the exact plan again.")
    {
    }
}

internal sealed record CurseForgeManagedContentPlanEvidence
{
    public required Guid ServerId { get; init; }
    public required PluginProviderKind Provider { get; init; }
    public required string ProjectId { get; init; }
    public required string VersionId { get; init; }
    public required Guid AuthorizationId { get; init; }
    public required string Digest { get; init; }

    public static CurseForgeManagedContentPlanEvidence From(
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId,
        ManagedContentPlanAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (serverId == Guid.Empty || provider != PluginProviderKind.CurseForge ||
            string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId) ||
            authorization.AuthorizationId == Guid.Empty || authorization.Digest.Length != 64 ||
            authorization.Digest.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                "The CurseForge dependency plan lacks an exact server, release, authorization, or SHA-256 digest.");
        return new CurseForgeManagedContentPlanEvidence
        {
            ServerId = serverId,
            Provider = provider,
            ProjectId = projectId,
            VersionId = versionId,
            AuthorizationId = authorization.AuthorizationId,
            Digest = authorization.Digest.ToLowerInvariant()
        };
    }

    public bool MatchesSelection(
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId) =>
        ServerId == serverId && Provider == provider &&
        ProjectId.Equals(projectId, StringComparison.Ordinal) &&
        VersionId.Equals(versionId, StringComparison.Ordinal);

    public bool Matches(
        Guid serverId,
        PluginProviderKind provider,
        string projectId,
        string versionId,
        ManagedContentPlanAuthorization authorization) =>
        MatchesSelection(serverId, provider, projectId, versionId) &&
        AuthorizationId == authorization.AuthorizationId &&
        Digest.Equals(authorization.Digest, StringComparison.OrdinalIgnoreCase);
}
