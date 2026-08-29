using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Keeps only the exact installed identity and local safety evidence required to update or roll back
/// a CurseForge-managed server. Search results, labels, changelogs, CDN URLs, and provider digests are
/// transient API data and must not become an offline provider cache.
/// </summary>
public static class CurseForgePersistencePolicy
{
    public const string LocalCreationHistoryDetail =
        "Installed from a locally verified package; provider response details were not retained.";
    public const string LocalUpdateHistoryDetail =
        "Updated from a locally verified package; exact rollback identity is stored separately.";
    public const string LocalSnapshotDescription =
        "Verified local recovery snapshot created before an update.";
    public const string LocalCreationFailureDetail =
        "Creation stopped; provider response details were not retained.";

    public static bool IsCurseForgeCreation(string creationKind) =>
        creationKind.Equals(InstallSourceType.CurseForgeServerPack.ToString(), StringComparison.Ordinal) ||
        creationKind.Equals(InstallSourceType.CurseForgeGeneratedPack.ToString(), StringComparison.Ordinal);

    public static string DurableUpdateDetail(
        UpdateProvider provider,
        UpdateOperationState state,
        string detail)
    {
        if (provider != UpdateProvider.CurseForge)
            return detail;
        return state switch
        {
            UpdateOperationState.Querying => "Verifying the reviewed provider identity.",
            UpdateOperationState.Snapshotting => LocalSnapshotDescription,
            UpdateOperationState.Downloading => "Downloading the reviewed package.",
            UpdateOperationState.Verifying => "Verifying the downloaded package.",
            UpdateOperationState.Extracting => "Extracting the reviewed package into isolated staging.",
            UpdateOperationState.PlanningMigration => "Classifying local persistent and managed files.",
            UpdateOperationState.BuildingCandidate => "Validating the local candidate launch profile.",
            UpdateOperationState.Switching => "Switching the active instance atomically.",
            UpdateOperationState.Starting => "Candidate switched; local validation remains pending.",
            UpdateOperationState.Failed => "Update preparation failed; provider response details were not retained.",
            _ => "Provider update operation in progress."
        };
    }

    public static ProviderIdentityOrigin IdentityOriginFor(ServerInstallRequest request)
    {
        if (request.PackProvider != UpdateProvider.CurseForge)
            return ProviderIdentityOrigin.Unknown;
        return request.SourceType == InstallSourceType.CurseForgeGeneratedPack &&
               string.IsNullOrWhiteSpace(request.PackProjectId)
            ? ProviderIdentityOrigin.ArchiveManifest
            : ProviderIdentityOrigin.ApiDerivedOperationalIdentity;
    }

    public static UpdateSource Minimize(UpdateSource source)
    {
        if (source.Provider != UpdateProvider.CurseForge)
            return source;
        return source with
        {
            ProjectName = "",
            ProjectSlug = "",
            InstalledVersionName = "",
            SourceUrl = "",
            AssetNamePattern = "",
            LastCheckedAt = null,
            DetectionEvidence =
                "Exact installed CurseForge project and file identifiers are retained only for safe updates and rollback; provider labels and download metadata are refreshed on request."
        };
    }

    public static VersionSnapshot Minimize(VersionSnapshot snapshot)
    {
        if (snapshot.SourceProvider != UpdateProvider.CurseForge)
            return snapshot;
        return snapshot with
        {
            VersionName = "",
            Source = "",
            Changelog = "",
            UpdateNotes = ""
        };
    }

    public static UpdateSource RestoreInstalledIdentity(UpdateSource source, VersionSnapshot snapshot)
    {
        var provider = snapshot.SourceProvider == UpdateProvider.None
            ? source.Provider
            : snapshot.SourceProvider;
        var projectId = string.IsNullOrWhiteSpace(snapshot.ProviderProjectId)
            ? source.ProjectId
            : snapshot.ProviderProjectId;
        var installedFileId = string.IsNullOrWhiteSpace(snapshot.ProviderFileId) &&
                              provider != UpdateProvider.CurseForge
            ? source.InstalledFileId
            : snapshot.ProviderFileId;
        var identityOrigin = snapshot.IdentityOrigin == ProviderIdentityOrigin.Unknown
            ? source.IdentityOrigin
            : snapshot.IdentityOrigin;
        return source with
        {
            Provider = provider,
            ProjectId = projectId,
            InstalledVersionId = snapshot.VersionId,
            InstalledVersionName = snapshot.VersionName,
            InstalledFileId = installedFileId,
            IdentityOrigin = identityOrigin,
            MinecraftVersion = snapshot.MinecraftVersion,
            Loader = snapshot.Loader,
            LoaderVersion = snapshot.LoaderVersion,
            InstalledAt = DateTimeOffset.UtcNow
        };
    }
}
