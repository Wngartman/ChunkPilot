using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Bounded, non-executing inspection and extraction for user-selected server packages.</summary>
public sealed partial class ServerImportInspectionService
{
    public const long MaximumCompressedBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    public const int MaximumEntries = 200_000;
    public const double MaximumCompressionRatio = 250;
    private const int MaximumPathLength = 768;
    private const int MaximumDepth = 32;
    private static readonly SearchValues<char> InvalidWindowsCharacters = SearchValues.Create("<>:\"|?*");

    public async Task<ServerImportInspection> InspectFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The selected server package was not found.", fullPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Choose a regular file rather than a symbolic link or reparse point.");
        if (info.Length is <= 0 or > MaximumCompressedBytes)
            throw new InvalidDataException("The selected package is empty or exceeds ChunkPilot's 4 GB review limit.");

        var extension = info.Extension.ToLowerInvariant();
        if (extension is not ".zip" and not ".mrpack" and not ".jar")
            throw new InvalidDataException("Choose a server ZIP, Modrinth .mrpack, CurseForge ZIP, or server JAR.");

        string sha256;
        await using (var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();

        if (extension == ".mrpack")
        {
            var pack = await new ModrinthPackServerService().InspectAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            return new ServerImportInspection
            {
                SourceKind = ServerImportSourceKind.ModrinthPack,
                DisplayName = pack.Name,
                Platform = pack.Loader,
                MinecraftVersion = pack.MinecraftVersion,
                LoaderVersion = pack.LoaderVersion,
                RequiredJavaMajor = pack.RequiredJavaMajor,
                SourceSizeBytes = info.Length,
                ExpandedSizeBytes = pack.IndexedServerBytes,
                FileCount = pack.RequiredServerFiles + pack.OptionalServerFiles,
                ServerRoot = ".",
                LaunchCandidates = ["Managed loader from modrinth.index.json"],
                Sha256 = sha256,
                CanInstall = pack.CanCreate,
                Limitation = pack.Limitation,
                Warnings = ["The exact indexed files and loader are verified again during installation."]
            };
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = ValidateArchive(archive);

        if (extension == ".jar")
            return InspectJar(info, sha256, entries, archive);
        return await InspectZipAsync(info, sha256, entries, archive, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExtractAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(destination);
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = ValidateArchive(archive);
        foreach (var validated in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination,
                validated.NormalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureChildPath(destination, target);
            if (validated.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = validated.Entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (output.Length != validated.Entry.Length)
                throw new InvalidDataException($"Archive entry changed while extracting: {validated.NormalizedPath}.");
        }
    }

    private static ServerImportInspection InspectJar(
        FileInfo info,
        string sha256,
        IReadOnlyList<ValidatedEntry> entries,
        ZipArchive archive)
    {
        var names = entries.Select(item => item.NormalizedPath).ToArray();
        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        var manifest = manifestEntry is null ? "" : ReadSmallText(manifestEntry, 128 * 1024);
        var mainClass = manifest.Split('\n').FirstOrDefault(line =>
            line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "";
        var classCount = names.Count(name => name.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
        var serverEvidence = names.Any(name => name.EndsWith("MinecraftServer.class", StringComparison.OrdinalIgnoreCase)) ||
                             mainClass.Contains("paperclip", StringComparison.OrdinalIgnoreCase) ||
                             mainClass.Contains("server", StringComparison.OrdinalIgnoreCase) ||
                             mainClass.Contains("launcher", StringComparison.OrdinalIgnoreCase);
        var clientEvidence = mainClass.Contains("client", StringComparison.OrdinalIgnoreCase) ||
                             names.Any(name => name.Equals("net/minecraft/client/Minecraft.class", StringComparison.OrdinalIgnoreCase));
        var platform = DetectPlatform(names, info.Name);
        var (minecraft, loader) = DetectVersions(names.Append(info.Name));
        var canInstall = manifestEntry is not null && classCount >= 25 && serverEvidence && !clientEvidence;
        return new ServerImportInspection
        {
            SourceKind = ServerImportSourceKind.ServerJar,
            DisplayName = Path.GetFileNameWithoutExtension(info.Name),
            Platform = platform,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            RequiredJavaMajor = TryJava(minecraft),
            SourceSizeBytes = info.Length,
            ExpandedSizeBytes = entries.Sum(item => item.Entry.Length),
            FileCount = entries.Count,
            ServerRoot = ".",
            LaunchCandidates = [info.Name],
            Sha256 = sha256,
            CanInstall = canInstall,
            Limitation = canInstall ? "" : "The JAR does not contain a trustworthy headless server entry point; client and installer JARs are not accepted.",
            Warnings = minecraft == "Unknown" ? ["Minecraft version was not encoded in a safely readable location."] : []
        };
    }

    private static async Task<ServerImportInspection> InspectZipAsync(
        FileInfo info,
        string sha256,
        IReadOnlyList<ValidatedEntry> entries,
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var names = entries.Where(item => !item.IsDirectory).Select(item => item.NormalizedPath).ToArray();
        var curseManifest = entries.FirstOrDefault(item =>
            item.NormalizedPath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var sourceKind = curseManifest is null ? ServerImportSourceKind.ServerArchive : ServerImportSourceKind.CurseForgePack;
        var platform = DetectPlatform(names, info.Name);
        var (minecraft, loader) = DetectVersions(names.Append(info.Name));
        var displayName = Path.GetFileNameWithoutExtension(info.Name);
        if (curseManifest is not null)
        {
            await using var manifestStream = curseManifest.Entry.Open();
            using var document = await JsonDocument.ParseAsync(manifestStream,
                new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false }, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("name", out var name)) displayName = name.GetString() ?? displayName;
            if (root.TryGetProperty("minecraft", out var mc))
            {
                if (mc.TryGetProperty("version", out var version)) minecraft = version.GetString() ?? minecraft;
                if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
                {
                    var id = loaders.EnumerateArray().Select(item => item.TryGetProperty("id", out var value)
                            ? value.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var split = id.Split('-', 2);
                        platform = split[0] switch
                        {
                            "forge" => "Forge", "neoforge" => "NeoForge", "fabric" => "Fabric",
                            "quilt" => "Quilt", _ => platform
                        };
                        loader = split.Length > 1 ? split[1] : loader;
                    }
                }
            }
        }
        var candidates = names.Where(IsLaunchCandidate).Take(12).ToArray();
        var serverRoot = SharedRoot(candidates.Length > 0 ? candidates : names.Take(100).ToArray());
        var hasScripts = names.Any(name => Path.GetExtension(name) is ".bat" or ".cmd" or ".ps1" or ".sh");
        var warnings = new List<string>();
        if (hasScripts) warnings.Add("Pack-provided scripts are copied as data but never executed by the importer.");
        if (sourceKind == ServerImportSourceKind.CurseForgePack)
            warnings.Add("A CurseForge client manifest is not itself proof of a complete dedicated-server package.");
        if (candidates.Length > 1) warnings.Add("Choose the intended launcher before installation.");
        var canInstall = candidates.Length > 0;
        return new ServerImportInspection
        {
            SourceKind = sourceKind,
            DisplayName = displayName,
            Platform = platform,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            RequiredJavaMajor = TryJava(minecraft),
            SourceSizeBytes = info.Length,
            ExpandedSizeBytes = entries.Sum(item => item.Entry.Length),
            FileCount = entries.Count(item => !item.IsDirectory),
            ModCount = names.Count(name => name.Contains("/mods/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)),
            PluginCount = names.Count(name => name.Contains("/plugins/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)),
            ContainsWorld = names.Any(name => name.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase) || name.Equals("level.dat", StringComparison.OrdinalIgnoreCase)),
            ServerRoot = serverRoot,
            LaunchCandidates = candidates,
            Sha256 = sha256,
            CanInstall = canInstall,
            Limitation = canInstall ? "" : "No safe standalone server launcher was found. Import a provider server-pack ZIP or a complete server folder.",
            Warnings = warnings
        };
    }

    private static IReadOnlyList<ValidatedEntry> ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is <= 0 or > MaximumEntries)
            throw new InvalidDataException($"The package must contain from 1 through {MaximumEntries:N0} entries.");
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ValidatedEntry>(archive.Entries.Count);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            RejectLink(entry);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var normalized = ValidateRelativePath(entry.FullName, isDirectory);
            if (!spellings.TryAdd(normalized, normalized))
                throw new InvalidDataException($"Duplicate or case-colliding archive destination: {normalized}.");
            if (!isDirectory)
            {
                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpandedBytes)
                    throw new InvalidDataException("The package exceeds ChunkPilot's 16 GB expanded-size limit.");
                if (entry.Length > 0 && (entry.CompressedLength <= 0 ||
                                         entry.Length / (double)entry.CompressedLength > MaximumCompressionRatio))
                    throw new InvalidDataException($"Archive entry exceeds the compression-ratio limit: {normalized}.");
            }
            result.Add(new(entry, normalized, isDirectory));
        }
        return result;
    }

    private static string ValidateRelativePath(string value, bool isDirectory)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return isDirectory ? "." : throw new InvalidDataException("The package contains an empty file path.");
        if (normalized.Length > MaximumPathLength || normalized.StartsWith('/') || Path.IsPathRooted(normalized) ||
            normalized.Contains(':'))
            throw new InvalidDataException($"Unsafe archive path: {value}.");
        var segments = normalized.Split('/');
        if (segments.Length > MaximumDepth)
            throw new InvalidDataException($"Archive path exceeds the {MaximumDepth}-segment depth limit: {value}.");
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith(' ') ||
                segment.EndsWith('.') || segment.AsSpan().ContainsAny(InvalidWindowsCharacters) ||
                segment.Any(character => character < ' ') || IsReservedDeviceName(segment))
                throw new InvalidDataException($"Unsafe Windows path segment in package: {segment}.");
            if (!segment.IsNormalized(NormalizationForm.FormC))
                throw new InvalidDataException($"Archive path is not Unicode-normalized: {value}.");
        }
        if (segments[0].Equals(".chunkpilot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Imported packages cannot write ChunkPilot's internal .chunkpilot directory.");
        return normalized;
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        const int unixHardLink = 0x8000;
        var unixType = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixType == unixSymbolicLink || (unixType != 0 && unixType != unixHardLink &&
                                             !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')) ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Packages cannot contain links or reparse points: {entry.FullName}.");
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLaunchCandidate(string path)
    {
        if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = path.ToLowerInvariant();
        if (lower.Contains("/mods/") || lower.StartsWith("mods/", StringComparison.Ordinal) || lower.Contains("/plugins/") ||
            lower.StartsWith("plugins/", StringComparison.Ordinal) || lower.Contains("/libraries/") || lower.StartsWith("libraries/", StringComparison.Ordinal) ||
            lower.Contains("installer") || lower.Contains("client")) return false;
        var file = Path.GetFileName(lower);
        return file == "server.jar" || file.Contains("server") || file.Contains("paper") || file.Contains("purpur") ||
               file.Contains("spigot") || file.Contains("fabric") || file.Contains("forge") || file.Contains("quilt");
    }

    private static string DetectPlatform(IEnumerable<string> paths, string extra = "")
    {
        var text = string.Join('\n', paths.Append(extra)).ToLowerInvariant();
        if (text.Contains("neoforge")) return "NeoForge";
        if (text.Contains("forge")) return "Forge";
        if (text.Contains("fabric")) return "Fabric";
        if (text.Contains("quilt")) return "Quilt";
        if (text.Contains("paper")) return "Paper";
        if (text.Contains("purpur")) return "Purpur";
        if (text.Contains("spigot") || text.Contains("bukkit")) return "Spigot";
        return "Vanilla";
    }

    private static (string Minecraft, string Loader) DetectVersions(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var match = VersionRegex().Match(path);
            if (match.Success) return (match.Groups["mc"].Value, match.Groups["loader"].Value);
        }
        return ("Unknown", "");
    }

    private static int TryJava(string minecraftVersion) =>
        JavaRuntimePolicy.TryRequiredMajorForMinecraft(minecraftVersion) ?? 21;

    private static string SharedRoot(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return ".";
        var first = paths[0].Split('/');
        var count = first.Length;
        foreach (var path in paths.Skip(1))
        {
            var segments = path.Split('/');
            count = Math.Min(count, segments.Length);
            var index = 0;
            while (index < count && first[index].Equals(segments[index], StringComparison.OrdinalIgnoreCase)) index++;
            count = index;
        }
        return count > 0 ? string.Join('/', first.Take(count)) : ".";
    }

    private static string ReadSmallText(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length > maximumBytes) throw new InvalidDataException($"Archive metadata is too large: {entry.FullName}.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive attempted to write outside the managed staging directory.");
    }

    private sealed record ValidatedEntry(ZipArchiveEntry Entry, string NormalizedPath, bool IsDirectory);

    [GeneratedRegex(@"(?<!\d)(?<mc>(?:1\.\d+(?:\.\d+)?)|(?:b1\.\d+(?:\.\d+)?))(?:[-_](?<loader>\d+(?:\.\d+)+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
