using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ChunkPilot.Infrastructure;

public sealed class ModrinthPackReader
{
    private const string IndexName = "modrinth.index.json";
    private static readonly SearchValues<char> InvalidWindowsCharacters = SearchValues.Create("<>:\"|?*");
    private readonly ModrinthPackLimits limits;

    public ModrinthPackReader(ModrinthPackLimits? limits = null)
    {
        this.limits = limits ?? new ModrinthPackLimits();
        this.limits.Validate();
    }

    public async Task<ModrinthPackArchive> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("The Modrinth pack archive does not exist.", fullPath);
        if (file.Length > limits.MaximumArchiveBytes)
            throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumArchiveBytes}-byte archive limit.");

        using var archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count > limits.MaximumArchiveEntries)
            throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumArchiveEntries}-entry archive limit.");

        ValidateArchiveNames(archive, cancellationToken);
        var index = archive.Entries.SingleOrDefault(entry => entry.FullName.Equals(IndexName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"A Modrinth pack must contain one root {IndexName} file.");
        if (index.Length <= 0 || index.Length > limits.MaximumManifestBytes)
            throw new InvalidDataException($"{IndexName} is empty or exceeds the {limits.MaximumManifestBytes}-byte limit.");

        ModrinthPackManifest manifest;
        await using (var input = index.Open())
        await using (var buffer = new MemoryStream(capacity: checked((int)index.Length)))
        {
            await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length != index.Length)
                throw new InvalidDataException($"{IndexName} ended before its declared size.");
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            }, cancellationToken).ConfigureAwait(false);
            manifest = ParseManifest(document.RootElement);
        }

        var destinationSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileEntry in manifest.Files)
            AddManifestDestination(destinationSpellings, fileEntry.RelativePath);

        var common = new List<ModrinthPackOverrideEntry>();
        var server = new List<ModrinthPackOverrideEntry>();
        long overrideBytes = 0;
        var overrideFiles = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetOverride(entry, out var layer, out var relativePath) || relativePath.Length == 0 ||
                string.IsNullOrEmpty(entry.Name))
                continue;
            if (layer is null)
                continue; // client-overrides is valid pack content but is never materialized on a server.

            RejectLinkEntry(entry);
            if (entry.Length < 0 || entry.Length > limits.MaximumOverrideFileBytes)
                throw new InvalidDataException($"Override {entry.FullName} exceeds the per-file size limit.");
            ValidateCompressionRatio(entry);
            overrideFiles++;
            if (overrideFiles > limits.MaximumOverrideFiles)
                throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumOverrideFiles}-file override limit.");
            overrideBytes = CheckedTotal(overrideBytes, entry.Length, "override");
            if (overrideBytes > limits.MaximumOverrideBytes)
                throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumOverrideBytes}-byte override limit.");

            var normalized = ValidateRelativePath(relativePath);
            AddLayeredDestination(destinationSpellings, normalized);
            var model = new ModrinthPackOverrideEntry
            {
                ArchivePath = entry.FullName,
                RelativePath = normalized,
                Layer = layer.Value,
                FileSize = entry.Length
            };
            (layer == ModrinthPackSourceLayer.CommonOverride ? common : server).Add(model);
        }

        return new ModrinthPackArchive
        {
            ArchivePath = fullPath,
            Manifest = manifest,
            CommonOverrides = common,
            ServerOverrides = server
        };
    }

    public ModrinthPackDownload ValidateDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || (!uri.IsDefaultPort && uri.Port != 443) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Modrinth pack downloads require an absolute HTTPS URL without credentials, a custom port, or a fragment.");

        var origin = uri.IdnHost.ToLowerInvariant() switch
        {
            "cdn.modrinth.com" => ModrinthPackDownloadOrigin.ModrinthCdn,
            "github.com" => ModrinthPackDownloadOrigin.GitHub,
            "raw.githubusercontent.com" => ModrinthPackDownloadOrigin.GitHubRaw,
            "gitlab.com" => ModrinthPackDownloadOrigin.GitLab,
            _ => throw new InvalidDataException($"Modrinth pack download host is not allowed: {uri.IdnHost}")
        };
        return new ModrinthPackDownload { Uri = uri, Origin = origin };
    }

    private ModrinthPackManifest ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{IndexName} must contain a JSON object.");
        var formatVersion = RequiredInt(root, "formatVersion");
        if (formatVersion != 1)
            throw new InvalidDataException($"Unsupported Modrinth pack format version: {formatVersion}.");
        var game = RequiredString(root, "game", 64);
        if (!game.Equals("minecraft", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported Modrinth pack game: {game}.");
        var versionId = RequiredString(root, "versionId", 512);
        var name = RequiredString(root, "name", 512);
        var summary = OptionalString(root, "summary", 8_192);
        var dependencies = ParseDependencies(RequiredProperty(root, "dependencies"));
        var files = ParseFiles(RequiredProperty(root, "files"));
        return new ModrinthPackManifest
        {
            FormatVersion = formatVersion,
            Game = game,
            VersionId = versionId,
            Name = name,
            Summary = summary,
            Dependencies = dependencies,
            Files = files
        };
    }

    private IReadOnlyDictionary<string, string> ParseDependencies(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Modrinth pack dependencies must be a JSON object.");
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length is 0 or > 64 || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Modrinth pack dependencies require bounded string identifiers and versions.");
            var value = property.Value.GetString()?.Trim() ?? "";
            if (value.Length is 0 or > 256 || !dependencies.TryAdd(property.Name, value))
                throw new InvalidDataException($"Invalid or duplicate Modrinth pack dependency: {property.Name}.");
        }
        if (!dependencies.ContainsKey("minecraft"))
            throw new InvalidDataException("A Modrinth pack must declare its Minecraft dependency.");
        return dependencies;
    }

    private IReadOnlyList<ModrinthPackFile> ParseFiles(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Modrinth pack files must be a JSON array.");
        var count = element.GetArrayLength();
        if (count > limits.MaximumManifestFiles)
            throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumManifestFiles}-file manifest limit.");

        var files = new List<ModrinthPackFile>(count);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long indexedBytes = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every Modrinth pack file must be a JSON object.");
            var path = ValidateRelativePath(RequiredString(
                item, "path", limits.MaximumRelativePathCharacters, trim: false));
            var key = PathKey(path);
            if (!paths.TryAdd(key, path))
                throw new InvalidDataException($"Duplicate or case-equivalent Modrinth pack path: {path}.");
            var fileSize = RequiredLong(item, "fileSize");
            if (fileSize < 0 || fileSize > limits.MaximumIndexedBytes)
                throw new InvalidDataException($"Invalid Modrinth pack file size for {path}.");
            indexedBytes = CheckedTotal(indexedBytes, fileSize, "indexed file");
            if (indexedBytes > limits.MaximumIndexedBytes)
                throw new InvalidDataException($"The Modrinth pack exceeds the {limits.MaximumIndexedBytes}-byte indexed-file limit.");

            var hashes = ParseHashes(RequiredProperty(item, "hashes"), path);
            var downloads = ParseDownloads(RequiredProperty(item, "downloads"), path);
            var client = ModrinthPackEnvironmentSupport.Required;
            var server = ModrinthPackEnvironmentSupport.Required;
            if (TryUniqueProperty(item, "env", out var environment))
            {
                if (environment.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"The environment for {path} must be a JSON object.");
                client = ParseEnvironment(environment, "client", path);
                server = ParseEnvironment(environment, "server", path);
            }
            files.Add(new ModrinthPackFile
            {
                RelativePath = path,
                FileSize = fileSize,
                Hashes = hashes,
                Downloads = downloads,
                ClientEnvironment = client,
                ServerEnvironment = server
            });
        }
        return files;
    }

    private static ModrinthPackHashes ParseHashes(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The hashes for {path} must be a JSON object.");
        var sha1 = RequiredString(element, "sha1", 40).ToLowerInvariant();
        var sha512 = RequiredString(element, "sha512", 128).ToLowerInvariant();
        if (!IsHex(sha1, 40) || !IsHex(sha512, 128))
            throw new InvalidDataException($"The file {path} requires valid SHA-1 and SHA-512 hashes.");
        return new ModrinthPackHashes { Sha1 = sha1, Sha512 = sha512 };
    }

    private IReadOnlyList<ModrinthPackDownload> ParseDownloads(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is 0 ||
            element.GetArrayLength() > limits.MaximumDownloadsPerFile)
            throw new InvalidDataException($"The file {path} requires one to {limits.MaximumDownloadsPerFile} download URLs.");
        var downloads = new List<ModrinthPackDownload>(element.GetArrayLength());
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"The file {path} contains a non-string download URL.");
            downloads.Add(ValidateDownloadUrl(value.GetString() ?? ""));
        }
        return downloads;
    }

    private static ModrinthPackEnvironmentSupport ParseEnvironment(
        JsonElement environment,
        string name,
        string path)
    {
        if (!TryUniqueProperty(environment, name, out var value))
            return ModrinthPackEnvironmentSupport.Required;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The {name} environment for {path} must be a string.");
        return value.GetString() switch
        {
            "required" => ModrinthPackEnvironmentSupport.Required,
            "optional" => ModrinthPackEnvironmentSupport.Optional,
            "unsupported" => ModrinthPackEnvironmentSupport.Unsupported,
            var invalid => throw new InvalidDataException($"Unknown {name} environment for {path}: {invalid}.")
        };
    }

    private void ValidateArchiveNames(ZipArchive archive, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/'))
                throw new InvalidDataException($"Unsafe Modrinth archive entry path: {name}.");
            var trimmed = name.EndsWith('/') ? name[..^1] : name;
            if (trimmed.Length > 0)
                _ = ValidateRelativePath(trimmed, allowInternalRoot: true);
            var key = PathKey(name);
            if (!names.TryAdd(key, name))
                throw new InvalidDataException($"Duplicate or case-equivalent Modrinth archive entry: {name}.");
            RejectLinkEntry(entry);
        }
    }

    private static bool TryGetOverride(
        ZipArchiveEntry entry,
        out ModrinthPackSourceLayer? layer,
        out string relativePath)
    {
        layer = null;
        relativePath = "";
        var slash = entry.FullName.IndexOf('/');
        if (slash < 0)
            return false;
        var root = entry.FullName[..slash];
        var expected = root.ToLowerInvariant() switch
        {
            "overrides" => "overrides",
            "server-overrides" => "server-overrides",
            "client-overrides" => "client-overrides",
            _ => ""
        };
        if (expected.Length == 0)
            return false;
        if (!root.Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Modrinth override directory casing must be exact: {root}.");
        relativePath = entry.FullName[(slash + 1)..];
        if (relativePath.EndsWith('/'))
            relativePath = relativePath[..^1];
        layer = expected switch
        {
            "overrides" => ModrinthPackSourceLayer.CommonOverride,
            "server-overrides" => ModrinthPackSourceLayer.ServerOverride,
            _ => null
        };
        return true;
    }

    private string ValidateRelativePath(string value, bool allowInternalRoot = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limits.MaximumRelativePathCharacters ||
            value.Contains('\\') || value.StartsWith('/') ||
            Path.IsPathRooted(value))
            throw new InvalidDataException($"Unsafe Modrinth pack path: {value}.");
        var segments = value.Split('/');
        if (segments.Length > limits.MaximumPathDepth)
            throw new InvalidDataException($"Modrinth pack path exceeds the {limits.MaximumPathDepth}-segment depth limit: {value}.");
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.AsSpan().ContainsAny(InvalidWindowsCharacters) || segment.Any(character => character < ' '))
                throw new InvalidDataException($"Unsafe Windows path segment in Modrinth pack: {segment}.");
            if (IsReservedDeviceName(segment))
                throw new InvalidDataException($"Reserved Windows device name in Modrinth pack path: {segment}.");
        }
        if (!allowInternalRoot && segments[0].Equals(".chunkpilot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Modrinth packs cannot write ChunkPilot's internal .chunkpilot directory.");
        return value;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private void ValidateCompressionRatio(ZipArchiveEntry entry)
    {
        if (entry.Length == 0)
            return;
        if (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio)
            throw new InvalidDataException($"Override {entry.FullName} exceeds the compression-ratio limit.");
    }

    private static void RejectLinkEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixType = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixType == unixSymbolicLink ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Modrinth packs cannot contain links or reparse points: {entry.FullName}.");
    }

    private static void AddManifestDestination(IDictionary<string, string> spellings, string path)
    {
        if (!spellings.TryAdd(PathKey(path), path))
            throw new InvalidDataException($"Duplicate Modrinth pack destination: {path}.");
    }

    private static void AddLayeredDestination(IDictionary<string, string> spellings, string path)
    {
        var key = PathKey(path);
        if (spellings.TryGetValue(key, out var existing) && !existing.Equals(path, StringComparison.Ordinal))
            throw new InvalidDataException($"Case-equivalent Modrinth pack destinations are unsafe on Windows: {existing} and {path}.");
        spellings[key] = path;
    }

    private static string PathKey(string path) => path.Normalize(NormalizationForm.FormC);

    private static long CheckedTotal(long current, long addition, string kind)
    {
        try
        {
            return checked(current + addition);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"The Modrinth pack's declared {kind} sizes overflow the supported range.", exception);
        }
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (!TryUniqueProperty(parent, name, out var value))
            throw new InvalidDataException($"Modrinth pack index is missing {name}.");
        return value;
    }

    private static bool TryUniqueProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name))
                continue;
            if (found)
                throw new InvalidDataException($"Modrinth pack index contains duplicate {name} properties.");
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static string RequiredString(JsonElement parent, string name, int maximumLength, bool trim = true)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Modrinth pack field {name} must be a string.");
        var source = value.GetString() ?? "";
        var text = trim ? source.Trim() : source;
        if (text.Length is 0 || text.Length > maximumLength)
            throw new InvalidDataException($"Modrinth pack field {name} is empty or too long.");
        return text;
    }

    private static string OptionalString(JsonElement parent, string name, int maximumLength)
    {
        if (!TryUniqueProperty(parent, name, out var value))
            return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Modrinth pack field {name} must be a string.");
        var text = value.GetString() ?? "";
        if (text.Length > maximumLength)
            throw new InvalidDataException($"Modrinth pack field {name} is too long.");
        return text;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"Modrinth pack field {name} must be an integer.");
        return number;
    }

    private static long RequiredLong(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidDataException($"Modrinth pack field {name} must be an integer.");
        return number;
    }
}
