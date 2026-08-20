using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface IManagedLoaderCertificationCatalog
{
    Task<ManagedLoaderVersionCatalog> GetVersionsAsync(
        ManagedLoaderPlatform platform,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<ManagedLoaderBuildCatalog> GetBuildsAsync(
        ManagedLoaderPlatform platform,
        string minecraftVersion,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

public sealed record ManagedLoaderCertificationEntry
{
    public ManagedLoaderPlatform Platform { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public string ArtifactSha256 { get; init; } = "";
    public int? JavaMajor { get; init; }
    public int? InstallerJavaMajor { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public VanillaCertificationResult Result { get; init; }
    public string Reason { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public string InstallerJavaPath { get; init; } = "";
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool StatusPingConfirmed { get; init; }
    public bool CleanStopConfirmed { get; init; }
    public bool ExpectedFilesConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public bool CleanupSucceeded { get; init; }
    public int RetryCount { get; init; }
    public string DiagnosticLog { get; init; } = "";
}

public sealed record ManagedLoaderCertificationLedger
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid CampaignId { get; init; } = Guid.NewGuid();
    public ManagedLoaderPlatform Platform { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ExplicitEulaAuthorization { get; init; }
    public DateTimeOffset? CatalogRetrievedAt { get; init; }
    public string LatestStableVersion { get; init; } = "";
    public IReadOnlyList<ManagedLoaderCertificationEntry> Entries { get; init; } = [];
}

public sealed record ManagedLoaderCertificationCampaignOptions
{
    public required string LedgerPath { get; init; }
    public bool ExplicitEulaAuthorization { get; init; }
    public string? ExactVersion { get; init; }
    public string? ExactLoaderVersion { get; init; }
    public int MaximumEntries { get; init; } = int.MaxValue;
    public bool Resume { get; init; } = true;
    public bool RetryFailed { get; init; }
    public bool Force { get; init; }
    public bool RefreshCatalog { get; init; }
    public TimeSpan PerVersionTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public long MinimumFreeSpaceBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// Sequential, resumable exact-runtime certification for the newest stable build advertised for
/// every stable Minecraft version in one official managed-loader catalog.
/// </summary>
public sealed class ManagedLoaderCertificationCampaign(
    IManagedLoaderCertificationCatalog catalogs,
    IManagedLoaderExactRuntimeCertifier runtime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ManagedLoaderCertificationLedger> RunAsync(
        ManagedLoaderPlatform platform,
        ManagedLoaderCertificationCampaignOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ExplicitEulaAuthorization)
            throw new InvalidOperationException(
                "Managed-loader runtime certification requires explicit disposable EULA authorization.");
        if (options.MaximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumEntries must be positive.");

        var catalog = await catalogs.GetVersionsAsync(platform, options.RefreshCatalog,
            cancellationToken).ConfigureAwait(false);
        if (!catalog.ProviderAvailable)
            throw new InvalidOperationException(catalog.UnavailableDetail.Length > 0
                ? catalog.UnavailableDetail
                : $"The official {platform} catalog is unavailable.");

        var allStable = catalog.Versions.Where(version => version.StableMinecraft)
            .OrderByDescending(version => MinecraftVersionClassification.NumericVersion(version.MinecraftVersion))
            .ThenByDescending(version => version.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateVersions = string.IsNullOrWhiteSpace(options.ExactVersion)
            ? allStable
            : catalog.Versions
                .OrderByDescending(version => MinecraftVersionClassification.NumericVersion(version.MinecraftVersion))
                .ThenByDescending(version => version.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var selected = candidateVersions.Where(version => string.IsNullOrWhiteSpace(options.ExactVersion) ||
                version.MinecraftVersion.Equals(options.ExactVersion, StringComparison.OrdinalIgnoreCase))
            .Take(options.MaximumEntries)
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("The requested Minecraft version is not in the official loader catalog.");

        var ledgerPath = Path.GetFullPath(options.LedgerPath);
        EnsureFreeSpace(ledgerPath, options.MinimumFreeSpaceBytes);
        var ledger = options.Resume && !options.Force ? ReadLedger(ledgerPath, platform) : null;
        ledger ??= new ManagedLoaderCertificationLedger
        {
            Platform = platform,
            ExplicitEulaAuthorization = true,
            CatalogRetrievedAt = catalog.RetrievedUtc,
            LatestStableVersion = allStable.FirstOrDefault()?.MinecraftVersion ?? ""
        };
        var entries = ledger.Entries.ToDictionary(entry => entry.MinecraftVersion,
            StringComparer.OrdinalIgnoreCase);
        ledger = Save(ledger, entries, ledgerPath);

        foreach (var version in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureFreeSpace(ledgerPath, options.MinimumFreeSpaceBytes);
            if (!version.IsSelectable)
            {
                entries[version.MinecraftVersion] = BlockedVersion(version);
                ledger = Save(ledger, entries, ledgerPath);
                progress?.Report($"{platform} {version.MinecraftVersion}: blocked ({entries[version.MinecraftVersion].Reason})");
                continue;
            }

            ManagedLoaderBuildCatalog buildCatalog;
            try
            {
                buildCatalog = await catalogs.GetBuildsAsync(platform, version.MinecraftVersion,
                    options.RefreshCatalog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                entries[version.MinecraftVersion] = Blocked(version, null,
                    VanillaCertificationResult.BlockedEnvironment, SecretRedactor.Redact(exception.Message));
                ledger = Save(ledger, entries, ledgerPath);
                continue;
            }

            var build = buildCatalog.Builds.FirstOrDefault(candidate => candidate.IsSelectable &&
                candidate.Channel == ManagedLoaderChannel.Stable &&
                (string.IsNullOrWhiteSpace(options.ExactLoaderVersion) ||
                 candidate.LoaderVersion.Equals(options.ExactLoaderVersion, StringComparison.OrdinalIgnoreCase)));
            if (build is null)
            {
                var candidate = buildCatalog.Builds.FirstOrDefault(item =>
                    item.Channel == ManagedLoaderChannel.Stable &&
                    (string.IsNullOrWhiteSpace(options.ExactLoaderVersion) ||
                     item.LoaderVersion.Equals(options.ExactLoaderVersion, StringComparison.OrdinalIgnoreCase)));
                var result = candidate is null
                    ? VanillaCertificationResult.BlockedMissingOfficialArtifact
                    : candidate.RequiredJavaMajor is null
                        ? VanillaCertificationResult.BlockedUnresolvedJava
                        : !candidate.HasProviderIntegrity &&
                          !ManagedLoaderPlatformStrategies.For(platform).AllowsArtifactWithoutProviderChecksum
                            ? VanillaCertificationResult.BlockedIncompleteIntegrityMetadata
                            : VanillaCertificationResult.BlockedUnresolvedLaunchProfile;
                var fallbackReason = result switch
                {
                    VanillaCertificationResult.BlockedIncompleteIntegrityMetadata =>
                        "The official loader artifact has no provider SHA-1 or SHA-256 integrity metadata.",
                    VanillaCertificationResult.BlockedUnresolvedJava =>
                        "ChunkPilot has not resolved the required Java major for this exact loader build.",
                    VanillaCertificationResult.BlockedMissingOfficialArtifact =>
                        "The official loader source returned no stable exact server artifact for this Minecraft version.",
                    _ => "The official loader source returned no selectable stable exact build."
                };
                var reason = candidate?.UnavailableReason.Length > 0
                    ? candidate.UnavailableReason
                    : buildCatalog.UnavailableDetail.Length > 0
                        ? buildCatalog.UnavailableDetail
                        : fallbackReason;
                entries[version.MinecraftVersion] = Blocked(version, candidate, result, reason);
                ledger = Save(ledger, entries, ledgerPath);
                progress?.Report($"{platform} {version.MinecraftVersion}: blocked ({reason})");
                continue;
            }

            if (entries.TryGetValue(version.MinecraftVersion, out var prior) &&
                !ShouldRun(prior, build, options))
            {
                progress?.Report($"{platform} {version.MinecraftVersion} / {build.LoaderVersion}: resume {prior.Result}");
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            progress?.Report($"{platform} {version.MinecraftVersion} / {build.LoaderVersion}: certifying exact runtime");
            try
            {
                var outcome = await runtime.CertifyAsync(build, true, options.PerVersionTimeout,
                    cancellationToken).ConfigureAwait(false);
                entries[version.MinecraftVersion] = FromOutcome(version, build, prior, started, outcome);
                progress?.Report($"{platform} {version.MinecraftVersion} / {build.LoaderVersion}: {outcome.Result}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                entries[version.MinecraftVersion] = Blocked(version, build,
                    VanillaCertificationResult.Cancelled,
                    "Certification was cancelled; resume preserves completed exact-version results.",
                    started, prior?.RetryCount ?? 0);
                Save(ledger, entries, ledgerPath);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                   InvalidDataException or UnauthorizedAccessException or
                                                   InvalidOperationException or TimeoutException)
            {
                entries[version.MinecraftVersion] = Blocked(version, build,
                    VanillaCertificationResult.BlockedEnvironment,
                    SecretRedactor.Redact(exception.Message), started, prior?.RetryCount ?? 0);
            }
            ledger = Save(ledger, entries, ledgerPath);
        }

        return Save(ledger, entries, ledgerPath);
    }

    public static IReadOnlyList<ManagedLoaderRuntimeCertificationEvidence.Entry> PassedEvidence(
        ManagedLoaderCertificationLedger ledger)
    {
        var passed = ledger.Entries.Where(entry => entry.Result == VanillaCertificationResult.Passed &&
                entry.ArtifactSha256.Length == 64 && entry.JavaMajor is not null)
            .OrderByDescending(entry => MinecraftVersionClassification.NumericVersion(entry.MinecraftVersion))
            .ThenByDescending(entry => entry.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recommended = passed.FirstOrDefault()?.MinecraftVersion;
        return passed.Select(entry => new ManagedLoaderRuntimeCertificationEvidence.Entry(
            entry.Platform, entry.MinecraftVersion, entry.LoaderVersion, entry.InstallerVersion,
            entry.ArtifactSha256.ToLowerInvariant(), entry.JavaMajor!.Value, entry.CompletedAt,
            entry.MinecraftVersion.Equals(recommended, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static bool ShouldRun(
        ManagedLoaderCertificationEntry prior,
        ManagedLoaderBuild build,
        ManagedLoaderCertificationCampaignOptions options)
    {
        if (options.Force) return true;
        var sameIdentity = prior.LoaderVersion.Equals(build.LoaderVersion, StringComparison.OrdinalIgnoreCase) &&
                           prior.InstallerVersion.Equals(build.InstallerVersion, StringComparison.OrdinalIgnoreCase) &&
                           (build.ArtifactSha256.Length == 0 ||
                            prior.ArtifactSha256.Equals(build.ArtifactSha256, StringComparison.OrdinalIgnoreCase));
        if (!sameIdentity) return true;
        if (prior.Result == VanillaCertificationResult.Passed) return false;
        return options.RetryFailed || prior.Result == VanillaCertificationResult.Cancelled;
    }

    private static ManagedLoaderCertificationEntry BlockedVersion(ManagedLoaderMinecraftVersion version)
    {
        var strategy = ManagedLoaderPlatformStrategies.For(version.Platform);
        var result = version.RequiresUserSuppliedMinecraftServerJar
            ? VanillaCertificationResult.BlockedMissingOfficialArtifact
            : version.RequiredJavaMajor is null
                ? VanillaCertificationResult.BlockedUnresolvedJava
                : !strategy.SupportsRuntimeCertification
                    ? VanillaCertificationResult.BlockedUnresolvedLaunchProfile
                    : VanillaCertificationResult.BlockedMissingOfficialArtifact;
        var reason = version.UnavailableReason.Length > 0
            ? version.UnavailableReason
            : strategy.CreationUnavailableReason.Length > 0
                ? strategy.CreationUnavailableReason
                : "This official loader entry is not selectable for exact runtime certification.";
        return Blocked(version, null, result, reason);
    }

    private static ManagedLoaderCertificationEntry Blocked(
        ManagedLoaderMinecraftVersion version,
        ManagedLoaderBuild? build,
        VanillaCertificationResult result,
        string reason,
        DateTimeOffset? startedAt = null,
        int priorRetryCount = 0) => new()
    {
        Platform = version.Platform,
        MinecraftVersion = version.MinecraftVersion,
        LoaderVersion = build?.LoaderVersion ?? "",
        InstallerVersion = build?.InstallerVersion ?? "",
        ArtifactSha256 = build?.ArtifactSha256 ?? "",
        JavaMajor = build?.RequiredJavaMajor ?? version.RequiredJavaMajor,
        InstallerJavaMajor = build is null
            ? version.RequiredJavaMajor
            : ManagedLoaderInstallerJavaPolicy.Resolve(
                build.Platform,
                build.InstallerJavaMajor,
                build.RequiredJavaMajor ?? version.RequiredJavaMajor ?? 0),
        StartedAt = startedAt ?? DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = result,
        Reason = reason,
        CleanupSucceeded = true,
        RetryCount = priorRetryCount + (startedAt is null ? 0 : 1)
    };

    private static ManagedLoaderCertificationEntry FromOutcome(
        ManagedLoaderMinecraftVersion version,
        ManagedLoaderBuild build,
        ManagedLoaderCertificationEntry? prior,
        DateTimeOffset started,
        ManagedLoaderRuntimeCertificationOutcome outcome) => new()
    {
        Platform = version.Platform,
        MinecraftVersion = version.MinecraftVersion,
        LoaderVersion = build.LoaderVersion,
        InstallerVersion = build.InstallerVersion,
        ArtifactSha256 = outcome.ArtifactSha256.Length > 0
            ? outcome.ArtifactSha256
            : build.ArtifactSha256,
        JavaMajor = build.RequiredJavaMajor,
        InstallerJavaMajor = ManagedLoaderInstallerJavaPolicy.Resolve(
            build.Platform, build.InstallerJavaMajor, build.RequiredJavaMajor ?? 0),
        StartedAt = started,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = outcome.Result,
        Reason = outcome.Reason,
        JavaPath = outcome.JavaPath,
        InstallerJavaPath = outcome.InstallerJavaPath,
        RuntimeLaunched = outcome.RuntimeLaunched,
        ReadinessConfirmed = outcome.ReadinessConfirmed,
        StatusPingConfirmed = outcome.StatusPingConfirmed,
        CleanStopConfirmed = outcome.CleanStopConfirmed,
        ExpectedFilesConfirmed = outcome.ExpectedFilesConfirmed,
        NoUnexpectedGuiConfirmed = outcome.NoUnexpectedGuiConfirmed,
        CleanupSucceeded = outcome.CleanupSucceeded,
        RetryCount = (prior?.RetryCount ?? 0) + 1,
        DiagnosticLog = outcome.DiagnosticLog
    };

    private static ManagedLoaderCertificationLedger Save(
        ManagedLoaderCertificationLedger ledger,
        IReadOnlyDictionary<string, ManagedLoaderCertificationEntry> entries,
        string path)
    {
        var updated = ledger with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Entries = entries.Values
                .OrderByDescending(entry => MinecraftVersionClassification.NumericVersion(entry.MinecraftVersion))
                .ThenByDescending(entry => entry.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return updated;
    }

    private static ManagedLoaderCertificationLedger? ReadLedger(string path, ManagedLoaderPlatform platform)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ledger = JsonSerializer.Deserialize<ManagedLoaderCertificationLedger>(File.ReadAllText(path), Json);
            return ledger is { SchemaVersion: ManagedLoaderCertificationLedger.CurrentSchemaVersion } &&
                   ledger.Platform == platform
                ? ledger
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureFreeSpace(string path, long minimumFreeSpaceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeSpaceBytes);
        var root = Path.GetPathRoot(path) ??
                   throw new InvalidOperationException("The certification ledger has no filesystem root.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < minimumFreeSpaceBytes)
            throw new IOException(
                $"Certification requires at least {minimumFreeSpaceBytes / (1024 * 1024)} MiB free on {drive.Name}; " +
                $"only {drive.AvailableFreeSpace / (1024 * 1024)} MiB is available.");
    }
}
