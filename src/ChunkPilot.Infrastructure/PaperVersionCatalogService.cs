using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Typed, cache-backed access to PaperMC's official Fill v3 project and exact-build inventories.
/// The service resolves metadata only; downloads remain owned by <see cref="ManagedServerInstaller"/>.
/// </summary>
public sealed partial class PaperVersionCatalogService
{
    public const string ProjectUrl = "https://fill.papermc.io/v3/projects/paper";
    private const int CacheSchemaVersion = 1;
    private const string VersionCacheFileName = "paper-version-catalog.json";

    private readonly AppDataPaths paths;
    private readonly HttpClient http;
    private readonly TimeSpan cacheLifetime;

    public PaperVersionCatalogService(
        AppDataPaths paths,
        HttpClient? httpClient = null,
        TimeSpan? cacheLifetime = null)
    {
        this.paths = paths;
        this.cacheLifetime = cacheLifetime ?? TimeSpan.FromHours(2);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
    }

    public async Task<PaperVersionCatalog> GetVersionsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(paths.CatalogCache, VersionCacheFileName);
        var cached = ReadCache<PaperVersionCatalog>(cachePath);
        if (cached is not null)
            cached = ApplyEvidence(cached);
        if (IsFresh(cached?.RetrievedUtc) && !forceRefresh)
            return cached! with { IsFromCache = true, IsStale = false };
        if (cached is not null && !forceRefresh)
            return cached with
            {
                IsFromCache = true,
                IsStale = true,
                UnavailableDetail = "Refreshing is required before this saved PaperMC version inventory is considered current."
            };

        try
        {
            var catalog = await RefreshVersionsAsync(cancellationToken).ConfigureAwait(false);
            WriteCache(cachePath, catalog);
            return catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            if (cached is not null)
                return cached with
                {
                    IsFromCache = true,
                    IsStale = true,
                    UnavailableDetail = "ChunkPilot could not reach PaperMC, so this is the version inventory it last saved. " +
                                        SecretRedactor.Redact(exception.Message)
                };
            return PaperVersionCatalog.Unavailable(
                "ChunkPilot could not reach PaperMC and has no saved Paper version inventory yet. " +
                SecretRedactor.Redact(exception.Message));
        }
    }

    public async Task<PaperBuildCatalog> GetBuildsAsync(
        string minecraftVersion,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionId(minecraftVersion);
        var cachePath = BuildCachePath(minecraftVersion);
        var cached = ReadCache<PaperBuildCatalog>(cachePath);
        if (cached is not null)
            cached = ApplyEvidence(cached);
        if (cached is not null &&
            !cached.MinecraftVersion.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            cached = null;
        if (IsFresh(cached?.RetrievedUtc) && !forceRefresh)
            return cached! with { IsFromCache = true, IsStale = false };
        if (cached is not null && !forceRefresh)
            return cached with
            {
                IsFromCache = true,
                IsStale = true,
                UnavailableDetail = "Refreshing is required before these saved Paper builds are considered current."
            };

        try
        {
            var catalog = await RefreshBuildsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
            WriteCache(cachePath, catalog);
            return catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            if (cached is not null)
                return cached with
                {
                    IsFromCache = true,
                    IsStale = true,
                    UnavailableDetail = "ChunkPilot could not reach PaperMC, so these are the builds it last saved. " +
                                        SecretRedactor.Redact(exception.Message)
                };
            return PaperBuildCatalog.Unavailable(minecraftVersion,
                "ChunkPilot could not reach PaperMC and has no saved builds for this version. " +
                SecretRedactor.Redact(exception.Message));
        }
    }

    private async Task<PaperVersionCatalog> RefreshVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(ProjectUrl, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("versions", out var groups) ||
            groups.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("PaperMC's project response did not contain a version inventory.");

        var versions = new List<PaperVersionOption>();
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var value in group.Value.EnumerateArray())
            {
                var id = value.GetString() ?? "";
                if (!SafeVersionId().IsMatch(id))
                    continue;
                var releaseKind = MinecraftVersionClassification.ReleaseKindFor(
                    id, id.Contains('-', StringComparison.Ordinal) ? "snapshot" : "release");
                var java = PaperJavaRuntimePolicy.RequiredMajor(id);
                versions.Add(new PaperVersionOption
                {
                    VersionId = id,
                    VersionGroup = group.Name,
                    ReleaseKind = releaseKind,
                    RequiredJavaMajor = java,
                    JavaEvidence = java is { } major ? PaperJavaRuntimePolicy.Evidence(major) : "",
                    SupportReason = releaseKind != MinecraftReleaseKind.Release
                        ? "ChunkPilot creates Paper servers from stable Minecraft releases only."
                        : java is null
                            ? "Paper's required Java version is not established for this release."
                            : "Choose an exact stable Paper build next."
                });
            }
        }
        if (versions.Count == 0)
            throw new InvalidDataException("PaperMC's project response contained no readable versions.");

        return ApplyEvidence(new PaperVersionCatalog
        {
            Versions = versions,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true
        });
    }

    private async Task<PaperBuildCatalog> RefreshBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"{ProjectUrl}/versions/{Uri.EscapeDataString(minecraftVersion)}/builds";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PaperMC's build response was not a build list.");

        var builds = document.RootElement.EnumerateArray()
            .Select(item => ReadBuild(item, minecraftVersion))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.BuildId)
            .ToArray();
        if (builds.Length == 0)
            throw new InvalidDataException($"PaperMC returned no readable builds for {minecraftVersion}.");

        return ApplyEvidence(new PaperBuildCatalog
        {
            MinecraftVersion = minecraftVersion,
            Builds = builds,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true
        });
    }

    private static PaperBuildOption? ReadBuild(JsonElement element, string minecraftVersion)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) || !id.TryGetInt32(out var buildId) || buildId <= 0)
            return null;
        var channelText = ReadString(element, "channel");
        var channel = Enum.TryParse<PaperBuildChannel>(channelText, true, out var parsed)
            ? parsed
            : PaperBuildChannel.Unknown;
        var download = default(JsonElement);
        var hasDownload = element.TryGetProperty("downloads", out var downloads) &&
                          downloads.ValueKind == JsonValueKind.Object &&
                          downloads.TryGetProperty("server:default", out download) &&
                          download.ValueKind == JsonValueKind.Object;
        var checksums = hasDownload && download.TryGetProperty("checksums", out var values) &&
                        values.ValueKind == JsonValueKind.Object
            ? values
            : default;
        var sha256 = checksums.ValueKind == JsonValueKind.Object ? ReadString(checksums, "sha256") : "";
        var url = hasDownload ? ReadString(download, "url") : "";
        var fileName = hasDownload ? ReadString(download, "name") : "";
        var size = hasDownload && download.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var parsedSize)
            ? parsedSize
            : (long?)null;
        return new PaperBuildOption
        {
            MinecraftVersion = minecraftVersion,
            BuildId = buildId,
            Channel = channel,
            PublishedAt = ReadDate(element, "time"),
            FileName = fileName,
            DownloadUrl = url,
            ServerSha256 = sha256,
            ServerSizeBytes = size,
            SupportReason = channel != PaperBuildChannel.Stable
                ? "PaperMC marks this build as pre-stable. It remains Experimental and requires a separate acknowledgement."
                : string.IsNullOrWhiteSpace(url) || size is not > 0 || sha256.Length != 64
                    ? "The official build metadata is missing the verified download information ChunkPilot requires."
                    : "Exact stable build from PaperMC."
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string BuildCachePath(string minecraftVersion)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(minecraftVersion)))[..20]
            .ToLowerInvariant();
        return Path.Combine(paths.CatalogCache, $"paper-builds-{identity}.json");
    }

    private bool IsFresh(DateTimeOffset? retrieved) =>
        retrieved is { } value && DateTimeOffset.UtcNow - value < cacheLifetime;

    private static void ValidateVersionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeVersionId().IsMatch(value))
            throw new ArgumentException("Select a Paper Minecraft version from the official catalog.", nameof(value));
    }

    private static T? ReadCache<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out var parsed) || parsed != CacheSchemaVersion)
                return null;
            return document.RootElement.GetProperty("catalog").Deserialize<T>(ProtocolJson.Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PaperVersionCatalog ApplyEvidence(PaperVersionCatalog catalog) => catalog with
    {
        Versions = catalog.Versions.Select(PaperRuntimeCertificationEvidence.Apply).ToArray()
    };

    private static PaperBuildCatalog ApplyEvidence(PaperBuildCatalog catalog) => catalog with
    {
        Builds = catalog.Builds.Select(PaperRuntimeCertificationEvidence.Apply).ToArray()
    };

    private static void WriteCache<T>(string path, T catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new { schemaVersion = CacheSchemaVersion, catalog }, ProtocolJson.Options);
            var temporary = path + ".partial";
            File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A valid provider result remains valid when the optional offline cache cannot be written.
        }
    }

    private static bool IsProviderFailure(Exception exception) => exception is
        HttpRequestException or JsonException or InvalidDataException or IOException or TaskCanceledException;

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersionId();
}
