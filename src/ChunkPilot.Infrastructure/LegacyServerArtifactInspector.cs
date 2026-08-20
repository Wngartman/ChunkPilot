using System.IO.Compression;
using System.Security.Cryptography;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Bounded, non-executing inspection of a user-owned historical server JAR.</summary>
public sealed class LegacyServerArtifactInspector
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const int MaximumEntries = 50_000;

    public async Task<UserSuppliedServerArtifact> InspectAsync(
        string path,
        string minecraftVersion,
        string officialSha1 = "",
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a Minecraft dedicated-server JAR file.");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The selected server JAR was not found.", fullPath);
        if (info.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException($"The selected server JAR must be between 1 byte and {MaximumBytes / 1024 / 1024} MB.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Choose a regular server JAR rather than a symbolic link or reparse point.");

        var hasManifest = false;
        var hasServerClass = false;
        var classEntries = 0;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            if (archive.Entries.Count is <= 0 or > MaximumEntries)
                throw new InvalidDataException($"The selected JAR has an invalid or excessive entry count ({archive.Entries.Count:N0}).");
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)) hasManifest = true;
                if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) classEntries++;
                if (name.Equals("net/minecraft/server/MinecraftServer.class", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("/MinecraftServer.class", StringComparison.OrdinalIgnoreCase))
                    hasServerClass = true;
            }
        }
        if (!hasManifest || !hasServerClass || classEntries < 25)
            throw new InvalidDataException(
                "This JAR does not contain the manifest and dedicated-server classes ChunkPilot expects. Client JARs are not accepted.");

        string sha1;
        string sha256;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
#pragma warning disable CA5350 // SHA-1 is computed only to compare with Mojang's historical artifact identity.
            sha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
#pragma warning restore CA5350
        }
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var officialMatch = officialSha1.Length == 40 && sha1.Equals(officialSha1, StringComparison.OrdinalIgnoreCase);
        return new UserSuppliedServerArtifact
        {
            NativePath = fullPath,
            FileName = info.Name,
            MinecraftVersion = minecraftVersion,
            SizeBytes = info.Length,
            Sha1 = sha1,
            Sha256 = sha256,
            MatchesOfficialHash = officialMatch,
            IdentityEvidence = officialMatch
                ? "The selected file matches Mojang's official SHA-1 for this exact dedicated-server artifact."
                : "Mojang publishes no official server hash for this target. The file remains user-supplied and must pass isolated runtime validation."
        };
    }
}
