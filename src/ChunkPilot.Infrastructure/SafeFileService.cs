using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;
using Microsoft.VisualBasic.FileIO;

namespace ChunkPilot.Infrastructure;

public sealed record TextWriteReceipt(
    string TargetRelativePath,
    string? RecoveryPath,
    bool TargetExisted);

public sealed class SafeFileService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".properties", ".txt", ".json", ".json5", ".toml", ".yaml", ".yml", ".cfg", ".conf",
        ".ini", ".xml", ".snbt", ".log", ".bat", ".cmd", ".ps1"
    };

    private readonly AppDataPaths paths;
    private readonly CanonicalPathLockManager pathLocks;

    public SafeFileService(AppDataPaths paths, CanonicalPathLockManager? pathLocks = null)
    {
        this.paths = paths;
        this.pathLocks = pathLocks ?? new CanonicalPathLockManager();
    }

    public IReadOnlyList<FileSystemEntry> List(string root, string relativePath = "")
    {
        var directory = ResolveWithinRoot(root, relativePath, mustExist: true);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        return Directory.EnumerateFileSystemEntries(directory)
            .Take(10_000)
            .Select(path =>
            {
                var isDirectory = Directory.Exists(path);
                var info = isDirectory ? null : new FileInfo(path);
                return new FileSystemEntry
                {
                    Name = Path.GetFileName(path),
                    RelativePath = Path.GetRelativePath(Path.GetFullPath(root), path),
                    IsDirectory = isDirectory,
                    SizeBytes = info?.Length ?? 0,
                    ModifiedAt = File.GetLastWriteTimeUtc(path)
                };
            })
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TextFileContent> ReadTextAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        var path = ResolveWithinRoot(root, relativePath, mustExist: true);
        if (!File.Exists(path))
            throw new FileNotFoundException("File was not found.", path);
        var info = new FileInfo(path);
        if (info.Length > 10 * 1024 * 1024)
            throw new IOException("Files larger than 10 MB are not opened in the integrated editor.");
        if (!TextExtensions.Contains(info.Extension))
            throw new IOException("This file type is not treated as editable text.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Take(Math.Min(bytes.Length, 4_096)).Any(value => value == 0))
            throw new IOException("The file appears to be binary.");
        var (encoding, bomLength, hasBom) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes.AsSpan(bomLength));
        var lineEnding = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            content.Contains('\n', StringComparison.Ordinal) ? "\n" : Environment.NewLine;
        return new TextFileContent
        {
            RelativePath = relativePath,
            Content = content,
            EncodingName = encoding.WebName,
            HasBom = hasBom,
            LineEnding = lineEnding,
            LoadedSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            LoadedLastWriteAt = File.GetLastWriteTimeUtc(path)
        };
    }

    public async Task WriteTextAtomicAsync(
        string root,
        TextFileContent content,
        bool createRecoveryCopy = true,
        CancellationToken cancellationToken = default)
        => _ = await WriteTextAtomicWithReceiptAsync(root, content, createRecoveryCopy, cancellationToken)
            .ConfigureAwait(false);

    public async Task<TextWriteReceipt> WriteTextAtomicWithReceiptAsync(
        string root,
        TextFileContent content,
        bool createRecoveryCopy = true,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveWithinRoot(root, content.RelativePath, mustExist: false);
        await using var pathLock = await pathLocks.AcquireAsync(target, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!TextExtensions.Contains(Path.GetExtension(target)))
            throw new IOException("This file type is not enabled for integrated text editing.");

        if (File.Exists(target) && !string.IsNullOrWhiteSpace(content.LoadedSha256))
        {
            var current = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
            var currentHash = Convert.ToHexString(SHA256.HashData(current));
            if (!currentHash.Equals(content.LoadedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "The file changed outside ChunkPilot after it was opened. Reload it before saving.");
        }

        var targetExisted = File.Exists(target);
        string? recoveryPath = null;
        if (targetExisted && createRecoveryCopy)
        {
            recoveryPath = CreateTextRecoveryPath(root, content.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            File.Copy(target, recoveryPath, overwrite: false);
        }

        var encoding = Encoding.GetEncoding(content.EncodingName,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var bytes = encoding.GetBytes(content.Content);
        var preamble = content.HasBom ? encoding.GetPreamble() : [];
        var temporary = target + $".chunkpilot-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (preamble.Length > 0)
                    await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(target))
                File.Replace(temporary, target, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return new TextWriteReceipt(content.RelativePath, recoveryPath, targetExisted);
    }

    public async Task RollbackTextWriteAsync(
        string root,
        TextWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var target = ResolveWithinRoot(root, receipt.TargetRelativePath, mustExist: false);
        await using var pathLock = await pathLocks.AcquireAsync(target, cancellationToken).ConfigureAwait(false);
        var failedCopy = CreateTextRecoveryPath(root, receipt.TargetRelativePath, "failed-activation");
        Directory.CreateDirectory(Path.GetDirectoryName(failedCopy)!);

        if (!receipt.TargetExisted)
        {
            if (File.Exists(target))
                File.Move(target, failedCopy);
            return;
        }

        if (string.IsNullOrWhiteSpace(receipt.RecoveryPath))
            throw new InvalidOperationException("The known-good configuration recovery copy is unavailable.");
        var recovery = Path.GetFullPath(receipt.RecoveryPath);
        if (!IsWithin(recovery, paths.Recovery) || !File.Exists(recovery))
            throw new InvalidOperationException("The known-good configuration recovery copy is unavailable.");
        if (File.Exists(target))
            File.Copy(target, failedCopy, overwrite: false);
        var temporary = target + $".chunkpilot-rollback-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(recovery, temporary, overwrite: false);
            if (File.Exists(target))
                File.Replace(temporary, target, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public string ResolveWithinRoot(string root, string relativePath, bool mustExist)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The path escapes the selected server folder.");
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("The requested path does not exist.", candidate);
        ValidateReparsePoints(canonicalRoot, candidate);
        return candidate;
    }

    public void CreateDirectory(string root, string relativePath) =>
        Directory.CreateDirectory(ResolveWithinRoot(root, relativePath, mustExist: false));

    public void Move(string root, string sourceRelativePath, string destinationRelativePath)
    {
        var source = ResolveWithinRoot(root, sourceRelativePath, mustExist: true);
        var destination = ResolveWithinRoot(root, destinationRelativePath, mustExist: false);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    public void DeleteToRecycleBin(string root, string relativePath)
    {
        var target = ResolveWithinRoot(root, relativePath, mustExist: true);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recycle Bin deletion is available only on Windows.");
        if (Directory.Exists(target))
            FileSystem.DeleteDirectory(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    public async Task ExtractZipAsync(string root, string zipRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        var zipPath = ResolveWithinRoot(root, zipRelativePath, mustExist: true);
        var destination = ResolveWithinRoot(root, destinationRelativePath, mustExist: false);
        Directory.CreateDirectory(destination);
        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe ZIP entry: {entry.FullName}");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var input = entry.Open();
            await using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            await input.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateReparsePoints(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                break;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("ChunkPilot will not follow a reparse point from the server file manager.");
        }
    }

    private static (Encoding Encoding, int BomLength, bool HasBom) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3, true);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return (Encoding.Unicode, 2, true);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return (Encoding.BigEndianUnicode, 2, true);
        return (new UTF8Encoding(false, true), 0, false);
    }

    private string CreateTextRecoveryPath(string root, string relativePath, string category = "text-edits")
    {
        var rootIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16];
        var safeRelative = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.Combine(paths.Recovery, category, rootIdentity,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}", safeRelative);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalCandidate = Path.GetFullPath(candidate);
        return canonicalCandidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
               canonicalCandidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
