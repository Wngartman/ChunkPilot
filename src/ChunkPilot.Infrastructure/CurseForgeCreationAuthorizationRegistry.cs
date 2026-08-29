using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Keeps the integrity evidence from a successful CurseForge client-manifest preflight inside the
/// current Agent lifetime. An exact authorization expires quickly and can start at most one
/// official or generated creation operation.
/// </summary>
public sealed class CurseForgeCreationAuthorizationRegistry
{
    internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    private const int MaximumOutstandingAuthorizations = 64;
    private const int MaximumRevocationTombstones = 4_096;
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private readonly Dictionary<Guid, Authorization> authorizations = [];
    private readonly Dictionary<Guid, long> revoked = [];

    public CurseForgeCreationAuthorizationRegistry(
        TimeProvider? clock = null,
        TimeSpan? lifetime = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.lifetime = lifetime ?? DefaultLifetime;
        if (this.lifetime <= TimeSpan.Zero || this.lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "CurseForge creation authorization must expire within one hour.");
    }

    public void Register(CurseForgeModpackPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var authorization = Authorization.From(result, clock.GetTimestamp());
        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            if (revoked.ContainsKey(authorization.OperationId))
                throw new InvalidOperationException(
                    "This exact CurseForge preflight review was revoked before it completed.");
            if (authorizations.ContainsKey(authorization.OperationId))
                throw new InvalidOperationException(
                    "This exact CurseForge preflight identity is already authorized in the current Agent lifetime.");
            if (authorizations.Count >= MaximumOutstandingAuthorizations)
                throw new InvalidOperationException(
                    "Too many unconsumed CurseForge preflight authorizations are active. Let an older review expire and try again.");
            authorizations.Add(authorization.OperationId, authorization);
        }
    }

    public ModpackCreationPlan Consume(ModpackCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SourceKind is not (ModpackCreationSource.CurseForgeOfficialServerPack or
            ModpackCreationSource.CurseForgeGeneratedCandidate))
            throw new ArgumentException(
                "Only a provider-backed CurseForge creation can consume this authorization.", nameof(plan));
        var operationId = plan.PreflightOperationId.GetValueOrDefault();
        if (operationId == Guid.Empty)
            throw new InvalidOperationException(
                "CurseForge creation requires the exact successful preflight authorization identity.");

        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            if (!authorizations.TryGetValue(operationId, out var authorization))
                throw new InvalidOperationException(
                    "The exact CurseForge preflight authorization is missing, expired, or was already used. Inspect the release again.");
            if (!authorization.Matches(plan))
                throw new InvalidOperationException(
                    "The CurseForge creation plan no longer matches its exact preflight evidence. Inspect the release again.");
            if (!authorizations.Remove(operationId))
                throw new InvalidOperationException(
                    "The exact CurseForge preflight authorization was already consumed.");
            return plan with
            {
                CurseForgeGeneratedPlan = authorization.GeneratedPackPlan is null
                    ? null
                    : CurseForgeGeneratedPackPlanService.ValidateAndClone(authorization.GeneratedPackPlan)
            };
        }
    }

    /// <summary>
    /// Revokes one exact unconsumed review. This is deliberately idempotent so cancellation,
    /// selection replacement and a failed pipe response can all race safely.
    /// </summary>
    public bool Revoke(Guid operationId)
    {
        if (operationId == Guid.Empty) return false;
        lock (sync)
        {
            var now = clock.GetTimestamp();
            RemoveExpired(now);
            var removed = authorizations.Remove(operationId);
            if (revoked.ContainsKey(operationId)) return removed;
            if (revoked.Count >= MaximumRevocationTombstones)
            {
                var oldest = revoked.OrderBy(pair => pair.Value).First();
                revoked.Remove(oldest.Key);
            }
            revoked.Add(operationId, now);
            return true;
        }
    }

    /// <summary>
    /// Agent-route gate. Local archives and non-CurseForge providers intentionally pass through;
    /// provider-backed CurseForge creation cannot pass without consuming exact evidence.
    /// </summary>
    public ModpackCreationPlan DemandAndConsumeIfRequired(ModpackCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SourceKind is ModpackCreationSource.CurseForgeOfficialServerPack or
            ModpackCreationSource.CurseForgeGeneratedCandidate)
            return Consume(plan);
        return plan;
    }

    internal int OutstandingCount
    {
        get
        {
            lock (sync)
            {
                RemoveExpired(clock.GetTimestamp());
                return authorizations.Count;
            }
        }
    }

    private void RemoveExpired(long nowTimestamp)
    {
        foreach (var pair in authorizations.Where(pair =>
                     clock.GetElapsedTime(pair.Value.IssuedTimestamp, nowTimestamp) >= lifetime).ToArray())
            authorizations.Remove(pair.Key);
        foreach (var pair in revoked.Where(pair =>
                     clock.GetElapsedTime(pair.Value, nowTimestamp) >= lifetime).ToArray())
            revoked.Remove(pair.Key);
    }

    private sealed record Authorization
    {
        public required Guid OperationId { get; init; }
        public required ModpackCreationSource SourceKind { get; init; }
        public required string ProjectId { get; init; }
        public required string ClientFileId { get; init; }
        public required string ServerPackFileId { get; init; }
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
        public required CurseForgeGeneratedPackPlan? GeneratedPackPlan { get; init; }
        public required long IssuedTimestamp { get; init; }

        public static Authorization From(
            CurseForgeModpackPreflightResult result,
            long issuedTimestamp)
        {
            if (result.State != CatalogReleasePreflightState.Ready || result.OperationId == Guid.Empty ||
                !PositiveId(result.ProjectId) || !PositiveId(result.ClientFileId) ||
                !ApprovedUrl(result.ClientDownloadUrl) || !Sha1(result.ClientSha1) ||
                !Sha256(result.ClientSha256) || result.ClientSizeBytes is <= 0 or > ServerImportInspectionService.MaximumCompressedBytes ||
                string.IsNullOrWhiteSpace(result.MinecraftVersion) || result.MinecraftVersion.Length > 80 ||
                string.IsNullOrWhiteSpace(result.Loader) || result.Loader.Length > 40 ||
                string.IsNullOrWhiteSpace(result.LoaderVersion) || result.LoaderVersion.Length > 80 ||
                result.RequiredJavaMajor <= 0)
                throw new InvalidDataException(
                    "A complete successful CurseForge preflight is required before creation can be authorized.");

            var official = result.ServerPackFileId.Length > 0;
            CurseForgeGeneratedPackPlan? generatedPlan = null;
            if (official)
            {
                if (!PositiveId(result.ServerPackFileId) || !ApprovedUrl(result.ServerPackDownloadUrl) ||
                    !Sha1(result.ServerPackSha1) ||
                    result.ServerPackSizeBytes is not { } serverPackSize || serverPackSize <= 0 ||
                    serverPackSize > ServerImportInspectionService.MaximumCompressedBytes ||
                    result.GeneratedPackPlan is not null)
                    throw new InvalidDataException(
                        "The successful CurseForge preflight lacks exact official server-pack evidence.");
            }
            else if (result.ServerPackDownloadUrl.Length > 0 || result.ServerPackSha1.Length > 0 ||
                     result.ServerPackSizeBytes is not null)
            {
                throw new InvalidDataException(
                    "The successful CurseForge preflight contains contradictory generated-candidate evidence.");
            }
            else
            {
                generatedPlan = result.GeneratedPackPlan is null
                    ? throw new InvalidDataException(
                        "The successful CurseForge preflight lacks its exact generated file plan.")
                    : CurseForgeGeneratedPackPlanService.ValidateAndClone(result.GeneratedPackPlan);
                if (!generatedPlan.MinecraftVersion.Equals(result.MinecraftVersion, StringComparison.Ordinal) ||
                    !generatedPlan.Loader.Equals(result.Loader, StringComparison.Ordinal) ||
                    !generatedPlan.LoaderVersion.Equals(result.LoaderVersion, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "The successful CurseForge preflight generated plan contradicts its platform identity.");
            }

            return new Authorization
            {
                OperationId = result.OperationId,
                SourceKind = official
                    ? ModpackCreationSource.CurseForgeOfficialServerPack
                    : ModpackCreationSource.CurseForgeGeneratedCandidate,
                ProjectId = result.ProjectId,
                ClientFileId = result.ClientFileId,
                ServerPackFileId = result.ServerPackFileId,
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
                GeneratedPackPlan = generatedPlan,
                IssuedTimestamp = issuedTimestamp
            };
        }

        public bool Matches(ModpackCreationPlan plan) =>
            plan.PreflightOperationId == OperationId &&
            plan.SourceKind == SourceKind &&
            plan.Provider == UpdateProvider.CurseForge &&
            plan.ProjectId.Equals(ProjectId, StringComparison.Ordinal) &&
            plan.VersionId.Equals(ClientFileId, StringComparison.Ordinal) &&
            plan.ServerPackFileId.Equals(ServerPackFileId, StringComparison.Ordinal) &&
            plan.Source.Equals(SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
                ? ServerPackDownloadUrl : ClientDownloadUrl, StringComparison.Ordinal) &&
            plan.ExpectedSha1.Equals(SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
                ? ServerPackSha1 : ClientSha1, StringComparison.OrdinalIgnoreCase) &&
            plan.ExpectedSha256.Equals(SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
                ? "" : ClientSha256, StringComparison.OrdinalIgnoreCase) &&
            plan.ExpectedSha512.Length == 0 &&
            plan.ExpectedSizeBytes == (SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack
                ? ServerPackSizeBytes : ClientSizeBytes) &&
            plan.VerifiedClientArchiveSha256.Equals(ClientSha256, StringComparison.OrdinalIgnoreCase) &&
            plan.MinecraftVersion.Equals(MinecraftVersion, StringComparison.Ordinal) &&
            plan.Loader.Equals(Loader, StringComparison.Ordinal) &&
            plan.LoaderVersion.Equals(LoaderVersion, StringComparison.Ordinal) &&
            plan.RequiredJavaMajor == RequiredJavaMajor &&
            plan.CurseForgeGeneratedPlan is null;

        private static bool ApprovedUrl(string value) =>
            value.Length <= 2_048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            CurseForgeApiClient.IsApprovedDownloadUri(uri);

        private static bool PositiveId(string value) =>
            value.Length <= 80 && long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0;

        private static bool Sha1(string value) =>
            value.Length == 40 && value.All(Uri.IsHexDigit);

        private static bool Sha256(string value) =>
            value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
