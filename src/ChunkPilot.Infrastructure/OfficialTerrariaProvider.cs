using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface ITerrariaServerProvider
{
    TerrariaReleaseDescriptor Release { get; }
    Task<TerrariaMaterializationResult> DownloadAndMaterializeAsync(
        string cacheRoot,
        string destination,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>First-party Windows Terraria dedicated-server acquisition and bounded extraction.</summary>
public sealed class OfficialTerrariaProvider : ITerrariaServerProvider, IDisposable
{
    public const string OfficialHost = "terraria.org";
    public const string CurrentVersion = "1.4.5.6";
    public const string CurrentReleaseId = "1456";
    public const long CurrentPackageSize = 45_635_619;
    public const string CurrentPackagePath = "/api/download/pc-dedicated-server/terraria-server-1456.zip";

    private const int MaximumEntries = 10_000;
    private const long MaximumEntryBytes = 600L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1_500L * 1024 * 1024;
    private const double MaximumCompressionRatio = 500;
    private static readonly SearchValues<char> InvalidWindowsCharacters =
        SearchValues.Create(['<', '>', ':', '"', '|', '?', '*']);
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public OfficialTerrariaProvider(HttpClient? httpClient = null)
    {
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = TimeSpan.FromMinutes(20) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3 Terraria-foundation");
    }

    public static TerrariaReleaseDescriptor CurrentRelease() => new()
    {
        Version = CurrentVersion,
        ReleaseId = CurrentReleaseId,
        ArtifactUrl = $"https://{OfficialHost}{CurrentPackagePath}",
        ExpectedSizeBytes = CurrentPackageSize,
        LastModifiedAt = new DateTimeOffset(2026, 3, 9, 18, 30, 10, TimeSpan.Zero),
        Provenance = "Official Re-Logic Terraria dedicated-server distribution from terraria.org.",
        IntegrityEvidence = "The official source publishes size/HTTP metadata but no first-party cryptographic checksum. ChunkPilot records a local SHA-256 after download.",
        Artifact = new TerrariaServerArtifact
        {
            ArchiveSubtree = CurrentReleaseId + "/Windows/",
            ChecksumLimitation = "Re-Logic does not publish a cryptographic checksum for this package; the recorded SHA-256 is local integrity evidence."
        },
        LaunchProfile = new TerrariaLaunchProfile
        {
            // Exact expressions are updated from isolated certification evidence before public support.
            ReadinessPattern = @"Server started|Listening on port|Type 'help'",
            SaveConfirmationPattern = @"World saved|Saving world|Backing up world file"
        },
        Capabilities = new TerrariaCapabilityProfile(),
        UpdateIdentity = new TerrariaUpdateIdentity
        {
            Version = CurrentVersion,
            ReleaseId = CurrentReleaseId
        }
    };

    public TerrariaReleaseDescriptor Release => CurrentRelease();

    public async Task<TerrariaMaterializationResult> DownloadAndMaterializeAsync(
        string cacheRoot,
        string destination,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var release = CurrentRelease();
        var artifact = await GetArtifactAsync(release, cacheRoot, progress, cancellationToken).ConfigureAwait(false);
        await ExtractWindowsServerAsync(artifact.Path, release, destination, progress, cancellationToken)
            .ConfigureAwait(false);
        return new TerrariaMaterializationResult
        {
            Release = release,
            ExecutablePath = Path.Combine(Path.GetFullPath(destination), "TerrariaServer.exe"),
            LocalSha256 = artifact.Sha256,
            DownloadedBytes = artifact.SizeBytes,
            CachePath = artifact.Path
        };
    }

    public async Task<TerrariaCachedArtifact> GetArtifactAsync(
        TerrariaReleaseDescriptor release,
        string cacheRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var releaseCache = Path.Combine(Path.GetFullPath(cacheRoot), "terraria", release.Version);
        Directory.CreateDirectory(releaseCache);
        var provenancePath = Path.Combine(releaseCache, "artifact.json");
        var cached = await TryReadCachedAsync(provenancePath, release, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var partial = Path.Combine(releaseCache, $"{release.ReleaseId}-{Guid.NewGuid():N}.partial");
        try
        {
            using var response = await http.GetAsync(release.ArtifactUrl, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateResponse(response, release);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            var started = DateTimeOffset.UtcNow;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > release.ExpectedSizeBytes)
                            throw new InvalidDataException("The Terraria package exceeded the official expected size.");
                        sha256.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        var elapsed = Math.Max((DateTimeOffset.UtcNow - started).TotalSeconds, 0.001);
                        progress?.Report(new InstallProgress
                        {
                            State = InstallState.Downloading,
                            Stage = CreationStage.DownloadingServer,
                            CurrentStep = "Downloading the official Terraria server package",
                            BytesDownloaded = total,
                            TotalBytes = release.ExpectedSizeBytes,
                            BytesPerSecond = total / elapsed,
                            OverallPercent = Math.Min(45, total * 45d / release.ExpectedSizeBytes)
                        });
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (total != release.ExpectedSizeBytes)
                throw new InvalidDataException($"The Terraria package size was {total:N0} bytes; expected {release.ExpectedSizeBytes:N0} bytes from the official release record.");
            var hash = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            var canonical = Path.Combine(releaseCache, hash + ".zip");
            if (File.Exists(canonical)) File.Delete(partial);
            else File.Move(partial, canonical);
            var result = new TerrariaCachedArtifact
            {
                Version = release.Version,
                ReleaseId = release.ReleaseId,
                SourceUrl = release.ArtifactUrl,
                Path = canonical,
                Sha256 = hash,
                SizeBytes = total,
                CachedAt = DateTimeOffset.UtcNow,
                HashAuthority = "Locally calculated by ChunkPilot; not an official Re-Logic checksum."
            };
            await WriteAtomicJsonAsync(provenancePath, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static async Task ExtractWindowsServerAsync(
        string archivePath,
        TerrariaReleaseDescriptor release,
        string destination,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var canonicalDestination = Path.GetFullPath(destination);
        if (File.Exists(canonicalDestination))
            throw new InvalidDataException("The Terraria extraction destination is a file.");
        Directory.CreateDirectory(canonicalDestination);
        if ((File.GetAttributes(canonicalDestination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Terraria extraction destination cannot be a link or reparse point.");

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumEntries)
            throw new InvalidDataException($"The Terraria archive must contain between 1 and {MaximumEntries:N0} entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        var root = release.ReleaseId + "/Windows/";
        var extractable = new List<(ZipArchiveEntry Entry, string Relative)>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveEntry(entry, names, ref expanded);
            if (!entry.FullName.StartsWith(root, StringComparison.Ordinal) || IsDirectory(entry)) continue;
            var relative = entry.FullName[root.Length..];
            if (relative.Length == 0) continue;
            ValidateRelativePath(relative);
            extractable.Add((entry, relative));
        }
        if (!extractable.Any(item => item.Relative.Equals("TerrariaServer.exe", StringComparison.Ordinal)))
            throw new InvalidDataException($"The official Windows subtree {root} does not contain TerrariaServer.exe.");

        long completedBytes = 0;
        for (var index = 0; index < extractable.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = extractable[index];
            var target = Path.GetFullPath(Path.Combine(canonicalDestination,
                item.Relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureWithin(canonicalDestination, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + $".chunkpilot-{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var source = item.Entry.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (output.Length != item.Entry.Length)
                        throw new InvalidDataException($"Terraria archive entry changed length while extracting: {item.Relative}.");
                }
                if (File.Exists(target))
                    throw new InvalidDataException($"The Terraria extraction target already exists: {item.Relative}.");
                File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            completedBytes += item.Entry.Length;
            progress?.Report(new InstallProgress
            {
                State = InstallState.Extracting,
                Stage = CreationStage.PreparingServerFiles,
                CurrentStep = $"Extracting {item.Relative}",
                BytesDownloaded = completedBytes,
                TotalBytes = extractable.Sum(value => value.Entry.Length),
                Detail = $"File {index + 1:N0} of {extractable.Count:N0}",
                OverallPercent = 45 + (index + 1) * 35d / extractable.Count
            });
        }
        await ValidateWindowsExecutableAsync(Path.Combine(canonicalDestination, "TerrariaServer.exe"), cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }

    private static async Task<TerrariaCachedArtifact?> TryReadCachedAsync(
        string provenancePath,
        TerrariaReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(provenancePath)) return null;
            var value = JsonSerializer.Deserialize<TerrariaCachedArtifact>(
                await File.ReadAllTextAsync(provenancePath, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            var cacheDirectory = Path.GetDirectoryName(Path.GetFullPath(provenancePath))!;
            var cachedPath = value is null ? "" : Path.GetFullPath(value.Path);
            if (value is null || !cachedPath.StartsWith(cacheDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !value.Version.Equals(release.Version, StringComparison.Ordinal) ||
                !value.ReleaseId.Equals(release.ReleaseId, StringComparison.Ordinal) ||
                !value.SourceUrl.Equals(release.ArtifactUrl, StringComparison.Ordinal) ||
                value.SizeBytes != release.ExpectedSizeBytes || !File.Exists(value.Path) ||
                new FileInfo(value.Path).Length != release.ExpectedSizeBytes)
                return null;
            await using var stream = File.OpenRead(cachedPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return hash.Equals(value.Sha256, StringComparison.OrdinalIgnoreCase) ? value : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void ValidateRelease(TerrariaReleaseDescriptor release)
    {
        if (release.Version != CurrentVersion || release.ReleaseId != CurrentReleaseId ||
            release.ExpectedSizeBytes != CurrentPackageSize)
            throw new InvalidDataException("The Terraria release does not match ChunkPilot's reviewed official descriptor.");
        if (!Uri.TryCreate(release.ArtifactUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != CurrentPackagePath ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("The Terraria artifact origin or path is not approved.");
    }

    private static void ValidateResponse(HttpResponseMessage response, TerrariaReleaseDescriptor release)
    {
        var final = response.RequestMessage?.RequestUri ?? new Uri(release.ArtifactUrl);
        if (final.Scheme != Uri.UriSchemeHttps || !final.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) ||
            final.AbsolutePath != CurrentPackagePath || !string.IsNullOrEmpty(final.Query) ||
            !string.IsNullOrEmpty(final.Fragment))
            throw new InvalidDataException("The official Terraria request redirected to an unapproved origin or path.");
        if (response.Content.Headers.ContentLength is { } length && length != release.ExpectedSizeBytes)
            throw new InvalidDataException("The official Terraria response did not match the reviewed package size.");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The official Terraria response used an unexpected content type: {mediaType ?? "missing"}.");
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry, ISet<string> names, ref long expanded)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/') ||
            name.Length > 1_024 || Path.IsPathRooted(name))
            throw new InvalidDataException($"Unsafe Terraria archive entry path: {name}.");
        var trimmed = name.EndsWith('/') ? name[..^1] : name;
        if (trimmed.Length > 0) ValidateRelativePath(trimmed);
        var key = name.Normalize(NormalizationForm.FormC).TrimEnd('/');
        if (!names.Add(key))
            throw new InvalidDataException($"Duplicate or case-equivalent Terraria archive entry: {name}.");
        RejectLinkEntry(entry);
        if (IsDirectory(entry)) return;
        if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            throw new InvalidDataException($"Terraria archive entry exceeds the per-file limit: {name}.");
        expanded = checked(expanded + entry.Length);
        if (expanded > MaximumExpandedBytes)
            throw new InvalidDataException("The Terraria archive exceeds the expanded-size limit.");
        if (entry.Length > 0 && (entry.CompressedLength <= 0 ||
                                 entry.Length / (double)entry.CompressedLength > MaximumCompressionRatio))
            throw new InvalidDataException($"Terraria archive entry exceeds the compression-ratio limit: {name}.");
    }

    private static void ValidateRelativePath(string value)
    {
        var segments = value.Split('/');
        if (segments.Length > 32)
            throw new InvalidDataException("The Terraria archive path exceeds the depth limit.");
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.AsSpan().ContainsAny(InvalidWindowsCharacters) || segment.Any(character => character < ' '))
                throw new InvalidDataException($"Unsafe Windows path segment in Terraria archive: {segment}.");
            if (IsReservedDeviceName(segment))
                throw new InvalidDataException($"Reserved Windows device name in Terraria archive: {segment}.");
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static void RejectLinkEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymlink = 0xA000;
        var unixType = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixType == unixSymlink || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Terraria archives cannot contain links or reparse points: {entry.FullName}.");
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/');

    private static void EnsureWithin(string root, string candidate)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A Terraria archive entry escaped the extraction root.");
    }

    private static async Task ValidateWindowsExecutableAsync(string executable, CancellationToken cancellationToken)
    {
        var info = new FileInfo(executable);
        if (!info.Exists || info.Length < 64 * 1024)
            throw new InvalidDataException("TerrariaServer.exe is missing or implausibly small.");
        var header = new byte[2];
        await using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            4_096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 2 ||
            header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("The extracted TerrariaServer.exe is not a Windows PE executable.");
    }

    private static async Task WriteAtomicJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(value, ProtocolJson.Options), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record TerrariaCachedArtifact
{
    public string Version { get; init; } = "";
    public string ReleaseId { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset CachedAt { get; init; }
    public string HashAuthority { get; init; } = "";
}
