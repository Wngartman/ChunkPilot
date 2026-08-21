using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Preflight and compact persistence rules for exact Ornithe headless certification.</summary>
public static class OrnitheHeadlessCertificationPolicy
{
    public const int EvidenceSchemaVersion = 1;

    public static HeadlessCertificationResult? Preflight(OrnitheHeadlessCertificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.Plan;
        if (!request.ExplicitDisposableEulaAuthorization)
            return HeadlessCertificationResult.BlockedEulaAuthorization;
        if (!plan.Build.HasHeadlessProfileContract || string.IsNullOrWhiteSpace(plan.MainClass) ||
            plan.Libraries.Count == 0 || plan.ClassPath.Count == 0)
            return HeadlessCertificationResult.FailedMaterialization;
        if (plan.MinecraftServerArtifact.Source == HistoricalMinecraftServerArtifactSource.UserSupplied &&
            string.IsNullOrWhiteSpace(plan.UserSuppliedArtifactToken))
            return HeadlessCertificationResult.BlockedMissingServerArtifact;
        if (plan.MinecraftServerArtifact.Source == HistoricalMinecraftServerArtifactSource.OfficialMojang &&
            !plan.MinecraftServerArtifact.IsAutomaticallyAcquirable)
            return HeadlessCertificationResult.BlockedIncompleteIntegrity;
        if (plan.Build.RequiredJavaMajor is not 8)
            return HeadlessCertificationResult.BlockedUnresolvedJava;
        if (request.Timeout < TimeSpan.FromSeconds(30) || request.Timeout > TimeSpan.FromMinutes(20))
            return HeadlessCertificationResult.FailedMaterialization;
        return null;
    }

    public static string Export(IEnumerable<OrnitheHeadlessCertificationEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var entries = evidence
            .OrderBy(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LoaderFamily)
            .ThenBy(item => item.LoaderVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return JsonSerializer.Serialize(new EvidenceManifest
        {
            SchemaVersion = EvidenceSchemaVersion,
            Entries = entries
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    public sealed record EvidenceManifest
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyList<OrnitheHeadlessCertificationEvidence> Entries { get; init; } = [];
    }
}
