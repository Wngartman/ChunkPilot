using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class JarInventoryService
{
    private string? ValidateReviewedProviderActivation(
        ServerDefinition server,
        PluginRelease release,
        ModPluginEntry? reviewedExisting,
        IReadOnlyList<ModPluginEntry> inventory,
        string destination)
    {
        var expectedKind = IsPluginEcosystem(server.Ecosystem)
            ? ManagedAddonKind.Plugin
            : ManagedAddonKind.Mod;
        if (release.Kind != expectedKind ||
            !Path.GetFileName(destination).Equals(release.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed provider release does not target this server content folder.");

        var sameProject = inventory.Where(entry =>
                entry.Provider == release.Provider &&
                entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.Ordinal))
            .ToArray();
        string? replacement = null;
        if (reviewedExisting is null)
        {
            if (sameProject.Length != 0)
                throw new InvalidOperationException(
                    "The provider add-on inventory changed after review. Nothing was changed.");
        }
        else
        {
            var exact = sameProject.Where(entry =>
                    entry.RelativePath.Equals(reviewedExisting.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    entry.Sha256.Equals(reviewedExisting.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    entry.ProviderVersionId.Equals(reviewedExisting.ProviderVersionId, StringComparison.Ordinal))
                .ToArray();
            if (sameProject.Length != 1 || exact.Length != 1 || reviewedExisting.Sha256.Length != 64)
                throw new InvalidOperationException(
                    "The exact same-project add-on reviewed for replacement changed before activation. Nothing was changed.");
            replacement = ResolveContentJarPath(server, reviewedExisting.RelativePath, mustExist: true);
        }

        var collisions = inventory.Where(entry =>
                entry.FileName.Equals(release.FileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var destinationRelativePath = Path.GetRelativePath(server.RootPath, destination);
        var authorizedDestination = reviewedExisting is not null &&
            reviewedExisting.RelativePath.Equals(destinationRelativePath, StringComparison.OrdinalIgnoreCase) &&
            collisions.Length == 1 &&
            collisions[0].Sha256.Equals(reviewedExisting.Sha256, StringComparison.OrdinalIgnoreCase) &&
            collisions[0].Provider == release.Provider &&
            collisions[0].ProviderProjectId.Equals(release.ProjectId, StringComparison.Ordinal);
        if (collisions.Length > (authorizedDestination ? 1 : 0) ||
            collisions.Any(entry =>
                entry.Provider != release.Provider ||
                !entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"The destination JAR filename '{release.FileName}' appeared after review or is owned by a local or different-project add-on. Nothing was changed.");

        ValidateOptionalRegularFileChain(server.RootPath, destination);
        if (PathEntry(destination) != PathEntryKind.Missing && !authorizedDestination)
            throw new InvalidOperationException(
                $"The destination JAR filename '{release.FileName}' appeared after review without exact same-project ownership. Nothing was changed.");
        return replacement;
    }

    private void EnsureUnchangedActivationState(
        ServerDefinition server,
        PluginRelease? reviewedRelease,
        ModPluginEntry? reviewedExisting,
        string destination,
        string? replacement,
        string expectedDestinationSha256,
        string expectedReplacementSha256)
    {
        EnsureSafeExistingParentDirectory(server.RootPath, destination);
        if (reviewedRelease is not null)
        {
            var currentReplacement = ValidateReviewedProviderActivation(
                server, reviewedRelease, reviewedExisting, InventoryCore(server), destination);
            if (!string.Equals(currentReplacement, replacement, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The exact provider activation target changed after review. Nothing was changed.");
        }
        EnsureExpectedFileState(
            server.RootPath, destination, expectedDestinationSha256,
            "The destination add-on changed immediately before activation. Nothing was changed.");
        if (replacement is not null && !replacement.Equals(destination, StringComparison.OrdinalIgnoreCase))
            EnsureExpectedFileState(
                server.RootPath, replacement, expectedReplacementSha256,
                "The exact same-project add-on changed immediately before activation. Nothing was changed.");
    }

    private JarInstallReceipt ActivatePreparedJar(
        ServerDefinition server,
        string temporary,
        string destination,
        string? replacement,
        string incomingSha256,
        string expectedDestinationSha256,
        string expectedReplacementSha256)
    {
        var previous = expectedDestinationSha256.Length == 64
            ? destination
            : expectedReplacementSha256.Length == 64
                ? replacement
                : null;
        var previousSha256 = expectedDestinationSha256.Length == 64
            ? expectedDestinationSha256
            : expectedReplacementSha256;
        var previousRelativePath = previous is null ? null : Path.GetRelativePath(server.RootPath, previous);
        string? adjacentBackup = null;
        string? recoveryPath = null;

        if (previous is null)
        {
            EnsureExpectedFileState(
                server.RootPath, destination, "",
                "The destination add-on appeared at the activation boundary. Nothing was changed.");
            File.Move(temporary, destination, overwrite: false);
            ValidateActivatedFileOrPreserve(server.RootPath, destination, incomingSha256);
        }
        else
        {
            adjacentBackup = destination + $".chunkpilot-previous-{Guid.NewGuid():N}.tmp";
            ValidateOptionalRegularFileChain(server.RootPath, adjacentBackup);
            try
            {
                EnsureExpectedFileState(
                    server.RootPath, previous, previousSha256,
                    "The exact reviewed add-on changed at the activation boundary. Nothing was changed.");
                if (previous.Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    File.Replace(temporary, destination, adjacentBackup, ignoreMetadataErrors: true);
                }
                else
                {
                    EnsureExpectedFileState(
                        server.RootPath, destination, "",
                        "The destination add-on appeared at the activation boundary. Nothing was changed.");
                    File.Move(previous, adjacentBackup, overwrite: false);
                    try
                    {
                        EnsureRegularFileUnchanged(
                            server.RootPath, adjacentBackup, previousSha256,
                            "The exact reviewed add-on changed while it was being staged for activation.");
                        File.Move(temporary, destination, overwrite: false);
                    }
                    catch
                    {
                        TryRestoreMovedFile(
                            server.RootPath, adjacentBackup, server.RootPath, previous);
                        throw;
                    }
                }

                if (!RegularFileSha256(server.RootPath, adjacentBackup).Equals(
                        previousSha256, StringComparison.OrdinalIgnoreCase))
                {
                    RestoreUnexpectedBoundaryFile(
                        server.RootPath, destination, adjacentBackup, previous, incomingSha256);
                    throw new IOException(
                        "The file present at the activation boundary was not the exact reviewed add-on. It was restored and preserved.");
                }
                ValidateActivatedFileOrPreserve(server.RootPath, destination, incomingSha256);

                var recoveryDirectory = EnsureRecoveryDirectory(
                    server, "content", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                recoveryPath = Path.Combine(recoveryDirectory, Path.GetFileName(previous));
                if (PathEntry(recoveryPath) != PathEntryKind.Missing)
                    recoveryPath = Path.Combine(
                        recoveryDirectory,
                        $"{Path.GetFileNameWithoutExtension(previous)}-{Guid.NewGuid():N}.jar");
                try
                {
                    File.Copy(adjacentBackup, recoveryPath, overwrite: false);
                    EnsureRegularFileUnchanged(
                        paths.Root, recoveryPath, previousSha256,
                        "The add-on recovery copy could not be verified.");
                    File.Delete(adjacentBackup);
                    adjacentBackup = null;
                }
                catch
                {
                    RollbackUncommittedActivation(
                        server.RootPath, paths.Root, destination, adjacentBackup!, previous, incomingSha256,
                        previousSha256, recoveryPath);
                    throw;
                }
            }
            finally
            {
                // A non-reparse adjacent backup is deliberately retained if an external actor made
                // the destination unsafe to restore. Silently deleting it would destroy recovery evidence.
                if (adjacentBackup is not null)
                    TryRestoreMovedFile(
                        server.RootPath, adjacentBackup, server.RootPath, previous);
            }
        }

        return new JarInstallReceipt(
            Path.GetRelativePath(server.RootPath, destination),
            previousRelativePath,
            recoveryPath,
            incomingSha256,
            previousSha256);
    }

    private static void RestoreUnexpectedBoundaryFile(
        string root,
        string destination,
        string backup,
        string previous,
        string incomingSha256)
    {
        if (!previous.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            TryRestoreMovedFile(root, backup, root, previous);
            return;
        }
        if (!RegularFileSha256(root, destination).Equals(incomingSha256, StringComparison.OrdinalIgnoreCase))
            return;
        var failedIncoming = destination + $".chunkpilot-rejected-{Guid.NewGuid():N}.tmp";
        File.Replace(backup, destination, failedIncoming, ignoreMetadataErrors: true);
        if (PathEntry(failedIncoming) == PathEntryKind.RegularFile &&
            Sha256(failedIncoming).Equals(incomingSha256, StringComparison.OrdinalIgnoreCase))
            File.Delete(failedIncoming);
    }

    private static void RollbackUncommittedActivation(
        string root,
        string recoveryRoot,
        string destination,
        string backup,
        string previous,
        string incomingSha256,
        string previousSha256,
        string? incompleteRecovery)
    {
        ValidateOptionalRegularFileChain(root, destination);
        if (PathEntry(destination) == PathEntryKind.RegularFile &&
            RegularFileSha256(root, destination).Equals(incomingSha256, StringComparison.OrdinalIgnoreCase))
            File.Delete(destination);
        TryRestoreMovedFile(root, backup, root, previous);
        if (!string.IsNullOrWhiteSpace(incompleteRecovery) &&
            IsWithin(incompleteRecovery, recoveryRoot))
        {
            ValidateOptionalRegularFileChain(recoveryRoot, incompleteRecovery);
            if (PathEntry(incompleteRecovery) == PathEntryKind.RegularFile &&
                Sha256(incompleteRecovery).Equals(previousSha256, StringComparison.OrdinalIgnoreCase))
                File.Delete(incompleteRecovery);
        }
    }

    private static void ValidateActivatedFileOrPreserve(string root, string destination, string incomingSha256)
    {
        ValidateRegularFileChain(root, destination);
        if (!RegularFileSha256(root, destination).Equals(incomingSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "The activated add-on path changed at the write boundary. The unexpected file was preserved in place.");
    }

    private static void EnsureExpectedFileState(
        string root,
        string path,
        string expectedSha256,
        string message)
    {
        if (expectedSha256.Length == 0)
        {
            ValidateOptionalRegularFileChain(root, path);
            if (PathEntry(path) != PathEntryKind.Missing)
                throw new IOException(message);
            return;
        }
        EnsureRegularFileUnchanged(root, path, expectedSha256, message);
    }

    private static void EnsureRegularFileUnchanged(
        string root,
        string path,
        string expectedSha256,
        string message)
    {
        ValidateRegularFileChain(root, path);
        if (expectedSha256.Length != 64 ||
            !RegularFileSha256(root, path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }
}
