using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Identity-bound exact-runtime evidence for official PaperMC builds.</summary>
public static class PaperRuntimeCertificationEvidence
{
    private const string ResourceName = "ChunkPilot.Infrastructure.Resources.paper-runtime-certification-v1.json";
    private static readonly Lazy<IReadOnlyList<Evidence>> Passed = new(Load);

    private sealed record Manifest(int SchemaVersion, IReadOnlyList<Evidence> Entries);
    private sealed record Evidence(
        string MinecraftVersion,
        int BuildId,
        string ArtifactSha256,
        long ArtifactSize,
        int JavaMajor,
        DateTimeOffset ValidatedAt,
        bool Recommended);

    public static PaperVersionOption Apply(PaperVersionOption option)
    {
        var evidence = Passed.Value
            .Where(item => item.MinecraftVersion.Equals(option.VersionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Recommended)
            .ThenByDescending(item => item.BuildId)
            .FirstOrDefault();
        if (evidence is null)
            return option;
        return option with
        {
            SupportTier = evidence.Recommended ? MinecraftVersionSupportTier.Recommended : MinecraftVersionSupportTier.Verified,
            SupportReason = evidence.Recommended
                ? $"Paper {option.VersionId} build {evidence.BuildId} passed exact runtime certification and is recommended."
                : $"Paper {option.VersionId} build {evidence.BuildId} passed exact runtime certification.",
            Certification = Certification(evidence)
        };
    }

    public static PaperBuildOption Apply(PaperBuildOption option)
    {
        var evidence = Passed.Value.FirstOrDefault(item =>
            item.MinecraftVersion.Equals(option.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
            item.BuildId == option.BuildId && item.ArtifactSize == option.ServerSizeBytes &&
            item.ArtifactSha256.Equals(option.ServerSha256, StringComparison.OrdinalIgnoreCase));
        if (evidence is null)
            return option;
        return option with
        {
            SupportTier = evidence.Recommended ? MinecraftVersionSupportTier.Recommended : MinecraftVersionSupportTier.Verified,
            SupportReason = evidence.Recommended
                ? "This exact stable Paper build passed runtime certification and is recommended."
                : "This exact stable Paper build passed runtime certification.",
            Certification = Certification(evidence)
        };
    }

    public static string Export(
        PaperBuildOption build,
        int javaMajor,
        VanillaRuntimeCertificationOutcome outcome,
        bool recommended)
    {
        if (outcome.Result != VanillaCertificationResult.Passed || !outcome.RuntimeLaunched ||
            !outcome.ReadinessConfirmed || !outcome.CleanStopConfirmed ||
            !outcome.ExpectedFilesConfirmed || !outcome.NoUnexpectedGuiConfirmed ||
            !outcome.CleanupSucceeded || !build.HasIntegrityMetadata)
            throw new InvalidOperationException("Only a complete exact Paper runtime pass can become production evidence.");
        var entry = new Evidence(
            build.MinecraftVersion,
            build.BuildId,
            build.ServerSha256.ToLowerInvariant(),
            build.ServerSizeBytes!.Value,
            javaMajor,
            DateTimeOffset.UtcNow,
            recommended);
        return JsonSerializer.Serialize(new Manifest(1, [entry]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    public static string Export(PaperCertificationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var passed = ledger.Entries.Where(entry =>
                entry.Result == VanillaCertificationResult.Passed && entry.RuntimeLaunched &&
                entry.ReadinessConfirmed && entry.CleanStopConfirmed && entry.ExpectedFilesConfirmed &&
                entry.NoUnexpectedGuiConfirmed && entry.CleanupSucceeded && entry.ArtifactSha256.Length == 64 &&
                entry.ArtifactSize > 0 && entry.BuildId > 0 && entry.JavaMajor is >= 8)
            .Select(entry => new Evidence(
                entry.MinecraftVersion,
                entry.BuildId,
                entry.ArtifactSha256.ToLowerInvariant(),
                entry.ArtifactSize,
                entry.JavaMajor!.Value,
                entry.CompletedAt,
                entry.MinecraftVersion.Equals(ledger.RecommendedVersion, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return JsonSerializer.Serialize(new Manifest(1, passed),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    private static MinecraftVersionCertification Certification(Evidence evidence) => new()
    {
        Level = MinecraftVersionCertificationLevel.RuntimeCertified,
        OfficialVersionRecord = true,
        OfficialServerArtifact = true,
        ArtifactIntegrityMetadata = true,
        JavaResolved = true,
        LaunchProfileResolved = true,
        RuntimeLaunched = true,
        ReadinessConfirmed = true,
        CleanShutdownConfirmed = true,
        ExpectedFilesConfirmed = true,
        NoUnexpectedGuiConfirmed = true,
        RuntimeValidatedAt = evidence.ValidatedAt,
        Evidence =
        [
            $"Official PaperMC build {evidence.BuildId} SHA-256 {evidence.ArtifactSha256}",
            $"Healthy 64-bit Java {evidence.JavaMajor}",
            "Exact Paper server reached readiness and remained stable on loopback",
            "Status, console, plugin-directory, clean-stop, and disposable-root checks passed"
        ]
    };

    private static IReadOnlyList<Evidence> Load()
    {
        try
        {
            using var stream = typeof(PaperRuntimeCertificationEvidence).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
                return [];
            var manifest = JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return manifest is { SchemaVersion: 1 } ? manifest.Entries : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }
}
