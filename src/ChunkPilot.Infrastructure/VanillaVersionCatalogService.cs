using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Authoritative Minecraft Java Edition inventory and creation evidence, sourced only from Mojang's
/// official version manifest and per-version metadata documents.
/// </summary>
public sealed class VanillaVersionCatalogService
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const int MaximumConcurrentMetadataRequests = 12;

    private const string CacheFileName = "vanilla-version-catalog.json";
    private const int CacheSchemaVersion = 3;

    private readonly AppDataPaths paths;
    private readonly HttpClient http;
    private readonly TimeSpan cacheLifetime;
    private readonly object refreshSync = new();
    private Task<VanillaVersionCatalog>? activeRefresh;

    public VanillaVersionCatalogService(
        AppDataPaths paths,
        HttpClient? httpClient = null,
        TimeSpan? cacheLifetime = null)
    {
        this.paths = paths;
        this.cacheLifetime = cacheLifetime ?? TimeSpan.FromHours(6);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 (local Windows server manager)");
    }

    private string CachePath => Path.Combine(paths.CatalogCache, CacheFileName);

    /// <summary>
    /// Returns releases only for the beginner surface, or the complete official inventory when
    /// <paramref name="includeSnapshots"/> is true. The persisted cache always contains all channels.
    /// A stale cache is rendered immediately while one deduplicated refresh runs in the background.
    /// </summary>
    public async Task<VanillaVersionCatalog> GetCatalogAsync(
        bool includeSnapshots = false,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var cached = ReadCache();
        var fresh = cached?.RetrievedUtc is { } retrieved && DateTimeOffset.UtcNow - retrieved < cacheLifetime;
        if (cached is not null && fresh && !forceRefresh)
            return Project(cached with { IsFromCache = true, IsStale = false }, includeSnapshots);

        if (cached is not null && !forceRefresh)
        {
            StartBackgroundRefresh(cached);
            return Project(cached with
            {
                IsFromCache = true,
                IsStale = true,
                UnavailableDetail = "Refreshing the saved Mojang version catalog in the background; ChunkPilot will keep the version list it last saw if Mojang is unavailable."
            }, includeSnapshots);
        }

        try
        {
            var refreshed = await GetOrStartRefreshAsync(cached, cancellationToken).ConfigureAwait(false);
            return Project(refreshed, includeSnapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            if (cached is not null && cached.Options.Count > 0)
                return Project(cached with
                {
                    IsFromCache = true,
                    IsStale = true,
                    UnavailableDetail =
                        "ChunkPilot could not reach Mojang, so this is the version catalog it last saved. "
                        + SecretRedactor.Redact(exception.Message)
                }, includeSnapshots);
            return VanillaVersionCatalog.Unavailable(
                "ChunkPilot could not reach Mojang and has no saved version catalog yet. "
                + SecretRedactor.Redact(exception.Message));
        }
    }

    private Task<VanillaVersionCatalog> GetOrStartRefreshAsync(
        VanillaVersionCatalog? cached,
        CancellationToken cancellationToken)
    {
        Task<VanillaVersionCatalog> refresh;
        lock (refreshSync)
        {
            if (activeRefresh is null || activeRefresh.IsCompleted)
                activeRefresh = RefreshAndCacheAsync(cached, CancellationToken.None);
            refresh = activeRefresh;
        }
        return cancellationToken.CanBeCanceled ? refresh.WaitAsync(cancellationToken) : refresh;
    }

    private void StartBackgroundRefresh(VanillaVersionCatalog cached)
    {
        var refresh = GetOrStartRefreshAsync(cached, CancellationToken.None);
        _ = refresh.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<VanillaVersionCatalog> RefreshAndCacheAsync(
        VanillaVersionCatalog? cached,
        CancellationToken cancellationToken)
    {
        var refreshed = await RefreshAsync(cached, cancellationToken).ConfigureAwait(false);
        WriteCache(refreshed);
        return refreshed;
    }

    private async Task<VanillaVersionCatalog> RefreshAsync(
        VanillaVersionCatalog? cached,
        CancellationToken cancellationToken)
    {
        using var manifest = await GetJsonAsync(ManifestUrl, cancellationToken).ConfigureAwait(false);
        if (!manifest.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Minecraft version manifest did not contain a version list.");

        var latestRelease = ReadString(manifest.RootElement, "latest", "release");
        var latestSnapshot = ReadString(manifest.RootElement, "latest", "snapshot");
        var entries = versions.EnumerateArray()
            .Select(ReadManifestEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidDataException("The Minecraft version manifest contained no readable entries.");

        var cachedById = (cached?.Options ?? [])
            .ToDictionary(option => option.VersionId, StringComparer.OrdinalIgnoreCase);
        var resolved = new ConcurrentDictionary<int, VanillaVersionOption>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentMetadataRequests
            },
            async (index, token) =>
            {
                var entry = entries[index];
                if (cachedById.TryGetValue(entry.VersionId, out var prior) &&
                    !string.IsNullOrWhiteSpace(entry.MetadataSha1) &&
                    entry.MetadataSha1.Equals(prior.MetadataSha1, StringComparison.OrdinalIgnoreCase))
                {
                    resolved[index] = Reassess(RehydrateDerivedProfiles(prior with
                    {
                        ReleaseType = entry.ReleaseType,
                        ReleaseKind = entry.ReleaseKind,
                        Channel = MinecraftVersionClassification.ChannelFor(entry.ReleaseKind),
                        ReleaseTime = entry.ReleaseTime,
                        MetadataTime = entry.MetadataTime,
                        MetadataUrl = entry.MetadataUrl,
                        MetadataSha1 = entry.MetadataSha1
                    }), latestRelease);
                    return;
                }

                resolved[index] = await ResolveAsync(entry, latestRelease, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var options = Enumerable.Range(0, entries.Length)
            .Select(index => resolved[index])
            .ToArray();
        var latestVerified = options.FirstOrDefault(option =>
            option.ReleaseKind == MinecraftReleaseKind.Release &&
            option.SupportTier is MinecraftVersionSupportTier.Recommended or MinecraftVersionSupportTier.Verified)
            ?.VersionId ?? "";

        return new VanillaVersionCatalog
        {
            Options = options,
            RetrievedUtc = DateTimeOffset.UtcNow,
            IsFromCache = false,
            IsStale = false,
            ProviderAvailable = true,
            ManifestLatestReleaseId = latestRelease,
            ManifestLatestSnapshotId = latestSnapshot,
            LatestVerifiedReleaseId = latestVerified
        };
    }

    internal static bool CachedDerivedProfilesAreComplete(VanillaVersionOption option)
    {
        var javaCanNowResolve = option.RequiredJavaMajor is null or < 8 &&
            (JavaRuntimePolicy.TryRequiredMajorForMinecraft(option.VersionId) is >= 8 ||
             option.ReleaseKind == MinecraftReleaseKind.Snapshot &&
             option.ReleaseTime is { } publishedAt &&
             publishedAt < new DateTimeOffset(2014, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var launchCanNowResolve = !option.LaunchProfile.IsResolved &&
            MinecraftLaunchProfileResolver.Resolve(option.VersionId, option.ReleaseKind, option.ReleaseTime).IsResolved;
        return !javaCanNowResolve && !launchCanNowResolve;
    }

    private static VanillaVersionOption RehydrateDerivedProfiles(VanillaVersionOption option)
    {
        var java = option.RequiredJavaMajor;
        var javaSource = option.JavaRequirementSource;
        if (java is null or < 8)
        {
            java = JavaRuntimePolicy.TryRequiredMajorForMinecraft(option.VersionId);
            if (java is null && option.ReleaseKind == MinecraftReleaseKind.Snapshot &&
                option.ReleaseTime is { } publishedAt &&
                publishedAt < new DateTimeOffset(2014, 1, 1, 0, 0, 0, TimeSpan.Zero))
                java = 8;
            if (java is >= 8) javaSource = JavaRequirementSource.ChunkPilotPolicy;
        }
        var launch = option.LaunchProfile.IsResolved
            ? option.LaunchProfile
            : MinecraftLaunchProfileResolver.Resolve(option.VersionId, option.ReleaseKind, option.ReleaseTime);
        return option with
        {
            RequiredJavaMajor = java,
            JavaRequirementSource = javaSource,
            LaunchProfile = launch
        };
    }

    private async Task<VanillaVersionOption> ResolveAsync(
        ManifestEntry entry,
        string latestRelease,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonAsync(entry.MetadataUrl, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var serverDownload = default(JsonElement);
            var hasServer = root.TryGetProperty("downloads", out var downloads) &&
                            downloads.ValueKind == JsonValueKind.Object &&
                            downloads.TryGetProperty("server", out serverDownload) &&
                            serverDownload.ValueKind == JsonValueKind.Object;
            var url = "";
            var sha1 = "";
            long? size = null;
            if (hasServer)
            {
                url = ReadString(serverDownload, "url");
                sha1 = ReadString(serverDownload, "sha1");
                size = serverDownload.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed)
                    ? parsed
                    : null;
                hasServer = Uri.TryCreate(url, UriKind.Absolute, out var artifactUri) &&
                            artifactUri.Scheme == Uri.UriSchemeHttps;
            }

            var (javaMajor, javaSource) = ReadJavaRequirement(
                root, entry.VersionId, entry.ReleaseKind, entry.ReleaseTime);
            var launchProfile = MinecraftLaunchProfileResolver.Resolve(
                entry.VersionId, entry.ReleaseKind, entry.ReleaseTime);
            var integrity = hasServer && size is > 0 && !string.IsNullOrWhiteSpace(sha1);
            var certification = MinecraftVersionCertificationPolicy.FromMetadata(
                true, hasServer, integrity, javaMajor, launchProfile);
            var option = new VanillaVersionOption
            {
                VersionId = entry.VersionId,
                Channel = MinecraftVersionClassification.ChannelFor(entry.ReleaseKind),
                ReleaseType = entry.ReleaseType,
                ReleaseKind = entry.ReleaseKind,
                ReleaseTime = entry.ReleaseTime,
                MetadataTime = entry.MetadataTime,
                MetadataUrl = entry.MetadataUrl,
                MetadataSha1 = entry.MetadataSha1,
                HasServerDownload = hasServer,
                ServerDownloadUrl = url,
                ServerSha1 = sha1,
                ServerSizeBytes = size,
                RequiredJavaMajor = javaMajor,
                JavaRequirementSource = javaSource,
                LaunchProfile = launchProfile,
                Certification = certification,
                Provenance = "Official Mojang version metadata",
                CertificationEvidence = certification.Evidence
            };
            return Reassess(option, latestRelease);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new VanillaVersionOption
            {
                VersionId = entry.VersionId,
                Channel = MinecraftVersionClassification.ChannelFor(entry.ReleaseKind),
                ReleaseType = entry.ReleaseType,
                ReleaseKind = entry.ReleaseKind,
                ReleaseTime = entry.ReleaseTime,
                MetadataTime = entry.MetadataTime,
                MetadataUrl = entry.MetadataUrl,
                MetadataSha1 = entry.MetadataSha1,
                Support = VanillaVersionSupport.UnsupportedByChunkPilot,
                SupportTier = MinecraftVersionSupportTier.Unavailable,
                SupportReason = "ChunkPilot could not read this version's official metadata.",
                Provenance = "Official Mojang version manifest",
                Warnings = [$"Metadata unavailable: {SecretRedactor.Redact(exception.Message)}"]
            };
        }
    }

    private static VanillaVersionOption Reassess(VanillaVersionOption option, string latestRelease)
    {
        // A manifest-only fallback means the per-version metadata could not be read. Preserve that
        // provider failure instead of turning missing knowledge into a false no-artifact claim.
        if (option.Provenance.Equals("Official Mojang version manifest", StringComparison.Ordinal))
            return option;
        var integrity = option.ServerSizeBytes is > 0 && !string.IsNullOrWhiteSpace(option.ServerSha1);
        var metadataCertification = MinecraftVersionCertificationPolicy.FromMetadata(
            true,
            option.HasServerDownload,
            integrity,
            option.RequiredJavaMajor,
            option.LaunchProfile);
        var certification = VanillaRuntimeCertificationEvidence.Apply(option, metadataCertification);
        option = option with
        {
            Certification = certification,
            CertificationEvidence = certification.Evidence
        };
        var assessment = VanillaSupportPolicy.Assess(
            option.ReleaseKind,
            option.HasServerDownload,
            integrity,
            option.RequiredJavaMajor,
            option.JavaRequirementSource,
            option.LaunchProfile,
            option.Certification,
            option.VersionId.Equals(latestRelease, StringComparison.OrdinalIgnoreCase));
        var exactFailure = certification.Limitations.FirstOrDefault(item =>
            item.StartsWith("Exact certification result:", StringComparison.Ordinal));
        assessment = ApplyExactRuntimeFailure(assessment, exactFailure);
        var warnings = new List<string>();
        if (assessment.Tier == MinecraftVersionSupportTier.Experimental)
            warnings.Add(assessment.Reason);
        if (option.ReleaseKind is MinecraftReleaseKind.Snapshot or MinecraftReleaseKind.PreRelease or
            MinecraftReleaseKind.ReleaseCandidate or MinecraftReleaseKind.ExperimentalSnapshot)
            warnings.Add("This is an in-development build. Worlds made on it may not open in a later release.");
        if (option.JavaRequirementSource == JavaRequirementSource.ChunkPilotPolicy)
            warnings.Add("Mojang's metadata does not state Java for this build, so ChunkPilot worked it out from its documented historical compatibility rule.");
        if (!option.LaunchProfile.IsResolved && !string.IsNullOrWhiteSpace(option.LaunchProfile.Evidence))
            warnings.Add(option.LaunchProfile.Evidence);
        return option with
        {
            SupportTier = assessment.Tier,
            Support = assessment.Compatibility,
            SupportReason = assessment.Reason,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    internal static (MinecraftVersionSupportTier Tier, VanillaVersionSupport Compatibility, string Reason)
        ApplyExactRuntimeFailure(
            (MinecraftVersionSupportTier Tier, VanillaVersionSupport Compatibility, string Reason) assessment,
            string? exactFailure) =>
        exactFailure is null
            ? assessment
            : (MinecraftVersionSupportTier.Unavailable,
                VanillaVersionSupport.UnsupportedByChunkPilot,
                exactFailure);

    private static (int? Major, JavaRequirementSource Source) ReadJavaRequirement(
        JsonElement root,
        string versionId,
        MinecraftReleaseKind releaseKind,
        DateTimeOffset? releaseTime)
    {
        if (root.TryGetProperty("javaVersion", out var java) &&
            java.ValueKind == JsonValueKind.Object &&
            java.TryGetProperty("majorVersion", out var major) &&
            major.TryGetInt32(out var parsed) && parsed >= 8)
            return (parsed, JavaRequirementSource.OfficialMetadata);

        if (releaseKind is MinecraftReleaseKind.Alpha or MinecraftReleaseKind.Beta)
        {
            var historical = JavaRuntimePolicy.TryRequiredMajorForMinecraft(versionId);
            return historical is >= 8
                ? (historical, JavaRequirementSource.ChunkPilotPolicy)
                : (null, JavaRequirementSource.Unknown);
        }
        var inferred = JavaRuntimePolicy.TryRequiredMajorForMinecraft(versionId);
        // Mojang's 2013 snapshot metadata predates the javaVersion block and uses week-based IDs
        // that cannot be parsed as a release family. These server artifacts target the same legacy
        // JVM generation as the surrounding 1.x releases; Java 8 is ChunkPilot's supported,
        // reproducible floor and exact runtime certification remains required before promotion.
        if (inferred is null && releaseKind == MinecraftReleaseKind.Snapshot &&
            releaseTime is { } publishedAt &&
            publishedAt < new DateTimeOffset(2014, 1, 1, 0, 0, 0, TimeSpan.Zero))
            inferred = 8;
        return inferred is >= 8
            ? (inferred, JavaRequirementSource.ChunkPilotPolicy)
            : (null, JavaRequirementSource.Unknown);
    }

    private static ManifestEntry? ReadManifestEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var id = ReadString(element, "id");
        var type = ReadString(element, "type");
        var url = ReadString(element, "url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var metadataUri) || metadataUri.Scheme != Uri.UriSchemeHttps)
            return null;
        return new ManifestEntry(
            id,
            type,
            MinecraftVersionClassification.ReleaseKindFor(id, type),
            url,
            ReadString(element, "sha1"),
            ReadDate(element, "releaseTime"),
            ReadDate(element, "time"));
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt == 0 && exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(125), cancellationToken).ConfigureAwait(false);
            }
        }
        throw last ?? new HttpRequestException("The official metadata request failed.");
    }

    private static VanillaVersionCatalog Project(VanillaVersionCatalog catalog, bool includeSnapshots)
    {
        var options = catalog.Options
            .Select(option => Reassess(option, catalog.ManifestLatestReleaseId))
            .ToArray();
        var reassessed = catalog with
        {
            Options = options,
            LatestVerifiedReleaseId = options.FirstOrDefault(option =>
                option.ReleaseKind == MinecraftReleaseKind.Release &&
                option.SupportTier is MinecraftVersionSupportTier.Recommended or MinecraftVersionSupportTier.Verified)
                ?.VersionId ?? ""
        };
        return includeSnapshots ? reassessed : reassessed with
        {
            Options = options.Where(option => option.ReleaseKind == MinecraftReleaseKind.Release).ToArray()
        };
    }

    private VanillaVersionCatalog? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out var parsed) || parsed != CacheSchemaVersion)
                return null;
            var catalog = document.RootElement.GetProperty("catalog")
                .Deserialize<VanillaVersionCatalog>(ProtocolJson.Options);
            return catalog is { Options.Count: > 0 } ? catalog : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or
                                          UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteCache(VanillaVersionCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(paths.CatalogCache);
            var payload = JsonSerializer.Serialize(
                new { schemaVersion = CacheSchemaVersion, catalog }, ProtocolJson.Options);
            var temporary = CachePath + ".partial";
            File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Catalog persistence is an offline optimisation. It cannot turn a valid provider result into failure.
        }
    }

    private static bool IsProviderFailure(Exception exception) => exception is
        HttpRequestException or JsonException or InvalidDataException or IOException or TaskCanceledException;

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ReadString(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var container) && container.ValueKind == JsonValueKind.Object
            ? ReadString(container, property)
            : "";

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private sealed record ManifestEntry(
        string VersionId,
        string ReleaseType,
        MinecraftReleaseKind ReleaseKind,
        string MetadataUrl,
        string MetadataSha1,
        DateTimeOffset? ReleaseTime,
        DateTimeOffset? MetadataTime);
}
