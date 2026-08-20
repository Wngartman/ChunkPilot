using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record PaperCertificationEntry
{
    public string MinecraftVersion { get; init; } = "";
    public int BuildId { get; init; }
    public string ArtifactSha256 { get; init; } = "";
    public long ArtifactSize { get; init; }
    public int? JavaMajor { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public VanillaCertificationResult Result { get; init; }
    public string Reason { get; init; } = "";
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool StatusPingConfirmed { get; init; }
    public bool CleanStopConfirmed { get; init; }
    public bool ExpectedFilesConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public bool CleanupSucceeded { get; init; }
    public string DiagnosticLog { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record PaperCertificationLedger
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid CampaignId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ExplicitEulaAuthorization { get; init; }
    public string RecommendedVersion { get; init; } = "";
    public IReadOnlyList<PaperCertificationEntry> Entries { get; init; } = [];
}

public sealed record PaperCertificationCampaignOptions
{
    public required string CacheRoot { get; init; }
    public required string LedgerPath { get; init; }
    public bool ExplicitEulaAuthorization { get; init; }
    public bool Resume { get; init; } = true;
    public bool Force { get; init; }
    public bool RetryFailed { get; init; }
    public TimeSpan PerVersionTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Certifies the newest official stable Paper build for every stable Minecraft version in PaperMC's inventory.
/// The campaign is sequential, resumable, hash-bound, and uses only disposable loopback roots.
/// </summary>
public sealed class PaperCertificationCampaign(
    PaperVersionCatalogService catalogs,
    IVanillaRuntimeCertifier runtime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PaperCertificationLedger> RunAsync(
        PaperVersionCatalog catalog,
        PaperCertificationCampaignOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ExplicitEulaAuthorization)
            throw new InvalidOperationException("Paper runtime certification requires explicit disposable EULA authorization.");
        var versions = catalog.Versions.Where(version => version.IsSelectable)
            .OrderByDescending(version => MinecraftVersionClassification.NumericVersion(version.VersionId))
            .ThenByDescending(version => version.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ledger = options.Resume && !options.Force ? ReadLedger(options.LedgerPath) : null;
        ledger ??= new PaperCertificationLedger
        {
            ExplicitEulaAuthorization = true,
            RecommendedVersion = versions.FirstOrDefault()?.VersionId ?? ""
        };
        var entries = ledger.Entries.ToDictionary(entry => entry.MinecraftVersion, StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PaperBuildCatalog builds;
            try
            {
                builds = await catalogs.GetBuildsAsync(version.VersionId, forceRefresh: options.Force, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                entries[version.VersionId] = Blocked(version.VersionId, VanillaCertificationResult.BlockedEnvironment,
                    SecretRedactor.Redact(exception.Message), version.RequiredJavaMajor);
                ledger = Save(ledger, entries, options.LedgerPath);
                continue;
            }
            var build = builds.Builds.Where(candidate => candidate.IsSelectable && candidate.Channel == PaperBuildChannel.Stable)
                .OrderByDescending(candidate => candidate.BuildId).FirstOrDefault();
            if (build is null)
            {
                entries[version.VersionId] = Blocked(version.VersionId,
                    VanillaCertificationResult.BlockedMissingOfficialArtifact,
                    builds.UnavailableDetail.Length > 0 ? builds.UnavailableDetail : "PaperMC has no integrity-complete stable build for this Minecraft version.",
                    version.RequiredJavaMajor);
                ledger = Save(ledger, entries, options.LedgerPath);
                progress?.Report($"{version.VersionId}: blocked (no official stable build)");
                continue;
            }
            if (entries.TryGetValue(version.VersionId, out var prior) &&
                !ShouldRun(prior, build, options))
            {
                progress?.Report($"{version.VersionId} build {build.BuildId}: resume {prior.Result}");
                continue;
            }
            if (version.RequiredJavaMajor is not { } javaMajor)
            {
                entries[version.VersionId] = Blocked(version.VersionId,
                    VanillaCertificationResult.BlockedUnresolvedJava,
                    "Paper's Java requirement is not established for this Minecraft version.", null, build);
                ledger = Save(ledger, entries, options.LedgerPath);
                continue;
            }

            progress?.Report($"{version.VersionId} build {build.BuildId}: downloading/verifying official artifact");
            var started = DateTimeOffset.UtcNow;
            try
            {
                var artifactSha1 = await CacheArtifactAsync(build, options.CacheRoot, cancellationToken).ConfigureAwait(false);
                var runtimeOption = RuntimeOption(version, build, javaMajor, artifactSha1);
                var runtimeOptions = new VanillaCertificationCampaignOptions
                {
                    CacheRoot = options.CacheRoot,
                    LedgerPath = Path.Combine(options.CacheRoot, "paper-runtime-internal-ledger.json"),
                    ExplicitEulaAuthorization = true,
                    PerVersionTimeout = options.PerVersionTimeout,
                    ExpectedGeneratedDirectory = "plugins"
                };
                var outcome = await runtime.CertifyAsync(runtimeOption, runtimeOptions, cancellationToken)
                    .ConfigureAwait(false);
                entries[version.VersionId] = FromOutcome(version, build, javaMajor, started, outcome);
                progress?.Report($"{version.VersionId} build {build.BuildId}: {outcome.Result}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                entries[version.VersionId] = new PaperCertificationEntry
                {
                    MinecraftVersion = version.VersionId,
                    BuildId = build.BuildId,
                    ArtifactSha256 = build.ServerSha256,
                    ArtifactSize = build.ServerSizeBytes ?? 0,
                    JavaMajor = javaMajor,
                    StartedAt = started,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = VanillaCertificationResult.Cancelled,
                    Reason = "Certification was cancelled; resume preserves completed exact-version results."
                };
                Save(ledger, entries, options.LedgerPath);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                entries[version.VersionId] = new PaperCertificationEntry
                {
                    MinecraftVersion = version.VersionId,
                    BuildId = build.BuildId,
                    ArtifactSha256 = build.ServerSha256,
                    ArtifactSize = build.ServerSizeBytes ?? 0,
                    JavaMajor = javaMajor,
                    StartedAt = started,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = VanillaCertificationResult.BlockedEnvironment,
                    Reason = SecretRedactor.Redact(exception.Message)
                };
            }
            ledger = Save(ledger, entries, options.LedgerPath);
        }
        return Save(ledger, entries, options.LedgerPath);
    }

    private static bool ShouldRun(PaperCertificationEntry prior, PaperBuildOption build, PaperCertificationCampaignOptions options)
    {
        if (options.Force) return true;
        var sameIdentity = prior.BuildId == build.BuildId && prior.ArtifactSize == build.ServerSizeBytes &&
                           prior.ArtifactSha256.Equals(build.ServerSha256, StringComparison.OrdinalIgnoreCase);
        if (!sameIdentity) return true;
        if (prior.Result == VanillaCertificationResult.Passed) return false;
        return options.RetryFailed || prior.Result == VanillaCertificationResult.Cancelled;
    }

    private static VanillaVersionOption RuntimeOption(
        PaperVersionOption version,
        PaperBuildOption build,
        int javaMajor,
        string sha1) => new()
    {
        VersionId = $"paper-{version.VersionId}-{build.BuildId}",
        Channel = VanillaReleaseChannel.Stable,
        ReleaseType = "release",
        ReleaseKind = MinecraftReleaseKind.Release,
        ReleaseTime = build.PublishedAt ?? DateTimeOffset.UtcNow,
        MetadataUrl = PaperVersionCatalogService.ProjectUrl,
        MetadataSha1 = new string('0', 40),
        HasServerDownload = true,
        ServerDownloadUrl = build.DownloadUrl,
        ServerSha1 = sha1,
        ServerSizeBytes = build.ServerSizeBytes,
        RequiredJavaMajor = javaMajor,
        JavaRequirementSource = JavaRequirementSource.ChunkPilotPolicy,
        Support = VanillaVersionSupport.Supported,
        SupportTier = MinecraftVersionSupportTier.Experimental,
        LaunchProfile = new MinecraftLaunchProfile
        {
            Kind = MinecraftLaunchProfileKind.ModernEulaNogui,
            Arguments = "--nogui",
            ReadinessPattern = "Done (",
            StopCommand = "stop",
            RequiresEulaFile = true,
            Evidence = "PaperMC headless launch contract.",
            Capabilities = new MinecraftVersionCapabilities { StatusQuery = true }
        },
        Provenance = build.Provenance
    };

    private static PaperCertificationEntry FromOutcome(
        PaperVersionOption version,
        PaperBuildOption build,
        int javaMajor,
        DateTimeOffset started,
        VanillaRuntimeCertificationOutcome outcome) => new()
    {
        MinecraftVersion = version.VersionId,
        BuildId = build.BuildId,
        ArtifactSha256 = build.ServerSha256,
        ArtifactSize = build.ServerSizeBytes ?? 0,
        JavaMajor = javaMajor,
        StartedAt = started,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = outcome.Result,
        Reason = outcome.Reason,
        RuntimeLaunched = outcome.RuntimeLaunched,
        ReadinessConfirmed = outcome.ReadinessConfirmed,
        StatusPingConfirmed = outcome.StatusPingConfirmed,
        CleanStopConfirmed = outcome.CleanStopConfirmed,
        ExpectedFilesConfirmed = outcome.ExpectedFilesConfirmed,
        NoUnexpectedGuiConfirmed = outcome.NoUnexpectedGuiConfirmed,
        CleanupSucceeded = outcome.CleanupSucceeded,
        DiagnosticLog = outcome.DiagnosticLog,
        Evidence = outcome.Evidence
    };

    private static PaperCertificationEntry Blocked(
        string version,
        VanillaCertificationResult result,
        string reason,
        int? java,
        PaperBuildOption? build = null) => new()
    {
        MinecraftVersion = version,
        BuildId = build?.BuildId ?? 0,
        ArtifactSha256 = build?.ServerSha256 ?? "",
        ArtifactSize = build?.ServerSizeBytes ?? 0,
        JavaMajor = java,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = result,
        Reason = reason,
        CleanupSucceeded = true
    };

    private static PaperCertificationLedger Save(
        PaperCertificationLedger ledger,
        IReadOnlyDictionary<string, PaperCertificationEntry> entries,
        string path)
    {
        var updated = ledger with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Entries = entries.Values.OrderByDescending(entry => MinecraftVersionClassification.NumericVersion(entry.MinecraftVersion))
                .ThenByDescending(entry => entry.MinecraftVersion, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return updated;
    }

    private static PaperCertificationLedger? ReadLedger(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ledger = JsonSerializer.Deserialize<PaperCertificationLedger>(File.ReadAllText(path), Json);
            return ledger?.SchemaVersion == PaperCertificationLedger.CurrentSchemaVersion ? ledger : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> CacheArtifactAsync(
        PaperBuildOption build,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var paperArtifacts = Path.Combine(cacheRoot, "paper-artifacts");
        Directory.CreateDirectory(paperArtifacts);
        var canonical = Path.Combine(paperArtifacts, build.ServerSha256.ToLowerInvariant() + ".jar");
        if (!await ValidArtifactAsync(canonical, build, cancellationToken).ConfigureAwait(false))
        {
            var partial = canonical + ".partial";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 Paper certification");
                using var response = await http.GetAsync(build.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (!await ValidArtifactAsync(partial, build, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The downloaded Paper artifact did not match PaperMC's SHA-256 and size.");
                File.Move(partial, canonical, true);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }
        await using var sha1Stream = File.OpenRead(canonical);
#pragma warning disable CA5350
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(sha1Stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
#pragma warning restore CA5350
        var runtimeArtifacts = Path.Combine(cacheRoot, "artifacts");
        Directory.CreateDirectory(runtimeArtifacts);
        var runtimePath = Path.Combine(runtimeArtifacts, sha1 + ".jar");
        if (!File.Exists(runtimePath)) File.Copy(canonical, runtimePath);
        return sha1;
    }

    private static async Task<bool> ValidArtifactAsync(
        string path,
        PaperBuildOption build,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != build.ServerSizeBytes) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return hash.Equals(build.ServerSha256, StringComparison.OrdinalIgnoreCase);
    }
}
