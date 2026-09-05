using System.Security.Cryptography;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Bounded operation inputs only. Not a provider cache; URLs, provider hashes and labels are never persisted.</summary>
public sealed class VerifiedCreationArchiveStore(AppDataPaths paths)
{
    public const long MaximumRetainedBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumRetainedOperations = 8;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private readonly string parent = Path.Combine(paths.Staging, "creation-inputs");

    public string DirectoryFor(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation identity is required.");
        return Path.Combine(parent, ServerCreationTransaction.StagingFolderName(operationId));
    }
    public string ArchiveFor(Guid operationId) => Path.Combine(DirectoryFor(operationId), "input.zip");

    public async Task<string> PrepareAsync(CreationOwnershipMarker owner, long expectedBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(parent);
        CreationStagingSafety.EnsureNoReparseTraversal(parent);
        var inventory = BoundedServerFileInventory.Capture(parent, 256, cancellationToken);
        if (Directory.EnumerateDirectories(parent).Take(MaximumRetainedOperations).Count() >= MaximumRetainedOperations ||
            expectedBytes <= 0 || expectedBytes > 4L * 1024 * 1024 * 1024 ||
            inventory.TotalBytes > MaximumRetainedBytes - expectedBytes)
            throw new IOException("Temporary creation input storage is full. Discard a completed failed attempt before downloading again.");
        var root = DirectoryFor(owner.OperationId);
        await CreationStagingSafety.PrepareOwnedDirectoryAsync(parent, root, owner, cancellationToken);
        return Path.Combine(root, "input.partial");
    }

    public async Task<CreationVerifiedInput> FinalizeAsync(CreationOwnershipMarker owner,
        ServerInstallRequest request, CancellationToken cancellationToken)
    {
        var root = DirectoryFor(owner.OperationId);
        CreationStagingSafety.RequireExactOwnership(root, owner.OperationId, owner.ServerId, owner.CanonicalDestination);
        var partial = Path.Combine(root, "input.partial");
        EnsureRegularFile(partial);
        var length = new FileInfo(partial).Length;
        if (length != request.ExpectedSizeBytes) throw new InvalidDataException("The verified input size changed.");
        await using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        await input.DisposeAsync();
        File.Move(partial, ArchiveFor(owner.OperationId), overwrite: false);
        return new CreationVerifiedInput
        {
            OperationId = owner.OperationId, ServerId = owner.ServerId,
            ProjectId = request.PackProjectId, ClientFileId = request.PackVersionId, ServerFileId = request.PackServerFileId,
            SizeBytes = length, LocalSha256 = sha256, CompletedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow + Retention
        };
    }

    public async Task<string> VerifyAsync(CreationJournalEntry entry, CancellationToken cancellationToken)
    {
        var proof = entry.VerifiedInput ?? throw new InvalidDataException("No verified input was retained.");
        if (proof.PolicyVersion != CreationVerifiedInput.CurrentPolicyVersion || proof.OperationId != entry.OperationId ||
            proof.ServerId != entry.ServerId || proof.SizeBytes <= 0 || proof.LocalSha256.Length != 64 ||
            proof.CompletedUtc == default || proof.CompletedUtc > DateTimeOffset.UtcNow ||
            proof.ExpiresUtc <= DateTimeOffset.UtcNow || proof.ExpiresUtc - proof.CompletedUtc > Retention + TimeSpan.FromSeconds(1))
            throw new InvalidDataException("The retained input is incomplete, expired, or has incompatible ownership/policy evidence.");
        var root = DirectoryFor(entry.OperationId);
        CreationStagingSafety.RequireExactOwnership(root, entry.OperationId, entry.ServerId, entry.CanonicalDestination);
        var archive = ArchiveFor(entry.OperationId);
        EnsureRegularFile(archive);
        await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != proof.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
            .Equals(proof.LocalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained archive changed; it cannot be reused. Discard this attempt and review a new download.");
        return archive;
    }

    public void Discard(CreationJournalEntry entry)
    {
        if (entry.ActivationBegan || entry.RegistrationBegan)
            throw new InvalidOperationException("An activated or active creation cannot discard retained input through failure recovery.");
        var root = DirectoryFor(entry.OperationId);
        if (Directory.Exists(root))
            CreationStagingSafety.DeleteOwnedTree(root, entry.OperationId, entry.ServerId, entry.CanonicalDestination);
    }

    private static void EnsureRegularFile(string path)
    {
        CreationStagingSafety.EnsureNoReparseTraversal(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The retained input is missing or redirected.");
    }
}
