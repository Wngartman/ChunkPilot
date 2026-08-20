using System.Text.Json;
using System.Text.Json.Serialization;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Compact, reviewed exact-runtime evidence promoted from the disposable certification ledger.
/// Entries are deliberately identity-bound: a changed Mojang metadata document or server artifact
/// falls back to metadata-only classification until that exact identity is certified again.
/// </summary>
public static class VanillaRuntimeCertificationEvidence
{
    private const string ResourceName = "ChunkPilot.Infrastructure.Resources.vanilla-runtime-certification-v1.json";
    private static readonly Lazy<IReadOnlyDictionary<string, Evidence>> Passed = new(Load);
    private static readonly JsonSerializerOptions Json = CreateJson();

    private sealed record Manifest(int SchemaVersion, IReadOnlyList<Evidence> Entries);

    private sealed record Evidence(
        string VersionId,
        string ArtifactSha1,
        string MetadataSha1,
        int JavaMajor,
        DateTimeOffset ValidatedAt,
        VanillaCertificationResult Result,
        string Reason,
        bool RuntimeLaunched,
        bool ReadinessConfirmed,
        bool? StatusPingConfirmed,
        bool CleanStopConfirmed,
        bool ExpectedFilesConfirmed,
        bool NoUnexpectedGuiConfirmed,
        bool CleanupSucceeded);

    public static MinecraftVersionCertification Apply(
        VanillaVersionOption option,
        MinecraftVersionCertification metadata)
    {
        if (!Passed.Value.TryGetValue(option.VersionId, out var evidence) ||
            !evidence.ArtifactSha1.Equals(option.ServerSha1, StringComparison.OrdinalIgnoreCase) ||
            !evidence.MetadataSha1.Equals(option.MetadataSha1, StringComparison.OrdinalIgnoreCase) ||
            option.RequiredJavaMajor != evidence.JavaMajor)
            return metadata;

        if (evidence.Result != VanillaCertificationResult.Passed ||
            !evidence.RuntimeLaunched || !evidence.ReadinessConfirmed ||
            evidence.StatusPingConfirmed is false ||
            !evidence.CleanStopConfirmed || !evidence.ExpectedFilesConfirmed ||
            !evidence.NoUnexpectedGuiConfirmed || !evidence.CleanupSucceeded)
        {
            return metadata with
            {
                Limitations = metadata.Limitations.Concat([
                    $"Exact certification result: {evidence.Result}. {evidence.Reason}"
                ]).Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        return metadata with
        {
            Level = MinecraftVersionCertificationLevel.RuntimeCertified,
            RuntimeLaunched = true,
            ReadinessConfirmed = true,
            CleanShutdownConfirmed = true,
            ExpectedFilesConfirmed = true,
            NoUnexpectedGuiConfirmed = true,
            RuntimeValidatedAt = evidence.ValidatedAt,
            Evidence = metadata.Evidence.Concat([
                $"Exact official server artifact SHA-1 {evidence.ArtifactSha1}",
                $"Healthy 64-bit Java {evidence.JavaMajor}",
                "Exact server reached readiness and remained stable on loopback",
                "Supported status and console checks completed",
                "Clean stop and disposable-root cleanup confirmed"
            ]).Distinct(StringComparer.Ordinal).ToArray(),
            Limitations = []
        };
    }

    /// <summary>
    /// Exports compact identity-bound terminal evidence. Exact passes promote support; exact failures
    /// retain their reason without including diagnostics, local paths, ports, or generated files.
    /// </summary>
    public static string Export(VanillaCertificationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var entries = ledger.Entries
            .Where(entry => entry.JavaMajor is >= 8 && entry.ArtifactSha1.Length == 40 &&
                            entry.MetadataSha1.Length == 40)
            .Select(entry => new Evidence(
                entry.VersionId,
                entry.ArtifactSha1.ToLowerInvariant(),
                entry.MetadataSha1.ToLowerInvariant(),
                entry.JavaMajor!.Value,
                entry.CompletedAt,
                entry.Result,
                entry.Reason,
                entry.RuntimeLaunched,
                entry.ReadinessConfirmed,
                entry.StatusPingConfirmed,
                entry.CleanStopConfirmed,
                entry.ExpectedFilesConfirmed,
                entry.NoUnexpectedGuiConfirmed,
                entry.CleanupSucceeded))
            .OrderBy(entry => entry.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return JsonSerializer.Serialize(new Manifest(1, entries), Json);
    }

    private static IReadOnlyDictionary<string, Evidence> Load()
    {
        try
        {
            using var stream = typeof(VanillaRuntimeCertificationEvidence).Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream is null)
                return new Dictionary<string, Evidence>(StringComparer.OrdinalIgnoreCase);
            var manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);
            if (manifest is not { SchemaVersion: 1 })
                return new Dictionary<string, Evidence>(StringComparer.OrdinalIgnoreCase);
            return manifest.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.VersionId))
                .GroupBy(entry => entry.VersionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.ValidatedAt).First(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, Evidence>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
