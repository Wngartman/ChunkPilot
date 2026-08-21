using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

/// <summary>Journal-like, ownership-proving server removal with a recoverable activation boundary.</summary>
public sealed class ServerDeletionCoordinator(
    ServerSupervisor supervisor,
    ChunkPilotStore store,
    AppDataPaths paths,
    RouterMappingCoordinator routerMappings,
    WindowsFirewallCoordinator firewallAccess,
    ManagedInstanceCopyService managedCopies)
{
    private readonly ConcurrentDictionary<Guid, ServerDeletionPreflight> preflights = new();

    public async Task<ServerDeletionPreflight> PreflightAsync(
        Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = supervisor.Get(serverId);
        var definition = server.Definition;
        var root = Canonical(definition.RootPath);
        var ownershipAssessment = await AssessOwnershipAsync(definition, reconcileExactCreationEvidence: true,
            cancellationToken).ConfigureAwait(false);
        var ownership = ownershipAssessment.Proven;
        var backups = await store.GetBackupsAsync(serverId, cancellationToken).ConfigureAwait(false);
        var managedBackups = backups.SelectMany(item => new[] { item.ArchivePath, item.ManifestPath })
            .Where(File.Exists).Where(path => IsUnder(Canonical(paths.Backups), Canonical(path)))
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var protectedPaths = backups.SelectMany(item => new[] { item.ArchivePath, item.ManifestPath })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Canonical).Where(path => !managedBackups.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var world = ResolveWorldPath(root);
        if (!IsUnder(root, world) && !SamePath(root, world)) protectedPaths.Add(world);
        var schedules = (await store.GetSchedulesAsync(cancellationToken).ConfigureAwait(false))
            .Count(item => item.ServerId == serverId && item.Enabled);
        var router = await store.GetRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false);
        var firewall = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        if (server.State is ServerState.BackingUp or ServerState.Restoring or ServerState.Saving or ServerState.Restarting)
            blockers.Add($"Wait for the current {server.State.ToString().ToLowerInvariant()} operation to finish.");
        if (definition.IsManaged && !ownership)
            blockers.Add("ChunkPilot cannot prove durable ownership of this folder. Data deletion is disabled. Remove the registration or create a verified managed copy; the original remains untouched.");
        if (firewall is { Configured: true } or { RemovalPending: true })
            blockers.Add("Remove this server's ChunkPilot-owned Windows Firewall rule before deleting its registration or data.");

        var result = new ServerDeletionPreflight
        {
            ServerId = serverId,
            ServerName = definition.Name,
            Platform = definition.Ecosystem.ToString(),
            Version = definition.MinecraftVersion,
            State = server.State,
            IsManaged = definition.IsManaged,
            OwnershipProven = ownership,
            OwnershipStatus = ownershipAssessment.Status,
            OwnershipDetail = ownershipAssessment.Detail,
            OwnershipEvidence = ownershipAssessment.Evidence,
            CanCreateManagedCopy = !ownership && CanCreateManagedCopy(definition, server.State, root),
            ManagedRoot = root,
            WorldLocation = world,
            BackupCount = backups.Count,
            ManagedBackupPaths = managedBackups,
            ProtectedExternalPaths = protectedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ActiveScheduleCount = schedules,
            InternetSharingConfigured = router is { HasActiveMapping: true } or { RemovalPending: true },
            FirewallRemovalRequired = firewall is { Configured: true } or { RemovalPending: true },
            Blockers = blockers
        };
        result = result with { ReviewFingerprint = Fingerprint(result) };
        preflights[result.Token] = result;
        RemoveExpired();
        return result;
    }

    public async Task<ServerDeletionReceipt> DeleteAsync(
        ServerDeletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!preflights.TryRemove(request.PreflightToken, out var prior) ||
            prior.ServerId != request.ServerId || prior.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Deletion preflight expired. Review the current server state again.");
        var current = await PreflightAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
        preflights.TryRemove(current.Token, out _);
        if (!string.Equals(current.ReviewFingerprint, prior.ReviewFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The server changed after deletion was reviewed. Review it again.");

        var server = supervisor.Get(request.ServerId);
        if (server.State == ServerState.Running)
        {
            var stopped = await server.StopAsync(saveFirst: true, source: "Delete server", cancellationToken)
                .ConfigureAwait(false);
            if (!stopped.Success) throw new InvalidOperationException(stopped.Message);
        }
        else if (server.State is not ServerState.Stopped and not ServerState.Crashed)
            throw new InvalidOperationException($"Wait until the server is stopped before deleting it (current state: {server.State}).");

        await firewallAccess.EnsureDeletionSafeAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
        var mappingNote = await routerMappings.PrepareForDeletionAsync(request.ServerId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(mappingNote))
            throw new InvalidOperationException(mappingNote.Trim());

        if (request.Mode == ServerDeletionMode.RemoveFromChunkPilot)
        {
            await supervisor.RemoveAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
            return new ServerDeletionReceipt
            {
                ServerId = request.ServerId,
                Mode = request.Mode,
                Removed = true,
                Detail = "The server was removed from ChunkPilot. Source files, worlds, and backup files were not changed."
            };
        }

        if (!current.IsManaged || !current.OwnershipProven)
            throw new InvalidOperationException("Only a marker-proven ChunkPilot-managed server can move or delete data.");
        if (request.Mode == ServerDeletionMode.Permanent &&
            (!request.ConfirmationName.Equals(current.ServerName, StringComparison.Ordinal) ||
             !request.AcknowledgeWorldDeletion || !request.AcknowledgeManagedBackupDeletion))
            throw new InvalidOperationException("Permanent deletion requires the exact server name and explicit world and managed-backup acknowledgements.");

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var recoveryRoot = Path.Combine(paths.ManagedServers, ".chunkpilot-recovery", request.ServerId.ToString("N"), stamp);
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryRoot)!);
        var originalRoot = Canonical(server.Definition.RootPath);
        if (request.Mode == ServerDeletionMode.Permanent && FindReparsePoint(originalRoot) is { } unsafeEntry)
            throw new InvalidOperationException(
                $"Permanent deletion refused ownership-uncertain reparse-point content: {Path.GetFileName(unsafeEntry)}. Move the server to Recovery instead.");
        var stagedServer = Path.Combine(recoveryRoot, "server");
        var movedBackups = new List<MovedPath>();
        var operationId = Guid.NewGuid();
        var journal = new ServerDeletionJournal(request.ServerId, request.Mode, originalRoot, stagedServer,
            recoveryRoot, movedBackups);
        await WriteJournalAsync(operationId, InstallState.Staging, journal,
            "Preparing the recoverable deletion boundary.", cancellationToken).ConfigureAwait(false);
        Directory.Move(originalRoot, stagedServer);
        await WriteJournalAsync(operationId, InstallState.Installing, journal,
            "The server root is held in Recovery while metadata is removed.", CancellationToken.None).ConfigureAwait(false);
        try
        {
            var backupRecovery = Path.Combine(paths.Recovery, "DeletedServers", request.ServerId.ToString("N"), stamp, "backups");
            foreach (var backup in current.ManagedBackupPaths)
            {
                if (!File.Exists(backup)) continue;
                Directory.CreateDirectory(backupRecovery);
                var destination = UniquePath(backupRecovery, Path.GetFileName(backup));
                movedBackups.Add(new MovedPath(backup, destination));
                journal = journal with { MovedBackups = movedBackups.ToArray() };
                await WriteJournalAsync(operationId, InstallState.Installing, journal,
                    "Moving exact ChunkPilot-managed backup files to Recovery.", CancellationToken.None).ConfigureAwait(false);
                File.Move(backup, destination);
            }

            await supervisor.RemoveAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            for (var index = movedBackups.Count - 1; index >= 0; index--)
                if (File.Exists(movedBackups[index].Staged) && !File.Exists(movedBackups[index].Original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(movedBackups[index].Original)!);
                    File.Move(movedBackups[index].Staged, movedBackups[index].Original);
                }
            if (Directory.Exists(stagedServer) && !Directory.Exists(originalRoot))
                Directory.Move(stagedServer, originalRoot);
            await store.CompleteOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (request.Mode == ServerDeletionMode.Permanent)
        {
            DeleteTreeWithoutFollowingReparsePoints(stagedServer);
            foreach (var moved in movedBackups) File.Delete(moved.Staged);
        }
        await store.CompleteOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        return new ServerDeletionReceipt
        {
            ServerId = request.ServerId,
            Mode = request.Mode,
            Removed = true,
            RecoveryPath = request.Mode == ServerDeletionMode.MoveToRecovery ? recoveryRoot : "",
            Detail = request.Mode == ServerDeletionMode.MoveToRecovery
                ? "The owned server and managed backups were moved to Recovery. External paths were not changed."
                : "The marker-proven managed server, world, and managed backup files were permanently deleted. External paths were not changed."
        };
    }

    public async Task<ManagedCopyConversionReceipt> CreateManagedCopyAsync(
        ManagedCopyConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!preflights.TryRemove(request.PreflightToken, out var prior) ||
            prior.ServerId != request.ServerId || prior.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Ownership review expired. Review the current server state again.");
        var current = await PreflightAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
        preflights.TryRemove(current.Token, out _);
        if (!string.Equals(current.ReviewFingerprint, prior.ReviewFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The server changed after ownership was reviewed. Review it again.");
        if (!current.IsManaged || current.OwnershipProven || !current.CanCreateManagedCopy)
            throw new InvalidOperationException("This server does not need, or cannot safely create, a verified managed copy.");

        var server = supervisor.Get(request.ServerId);
        if (server.State is not ServerState.Stopped and not ServerState.Crashed)
            throw new InvalidOperationException("Stop the server before creating a managed copy.");
        var original = server.Definition;
        var originalRoot = Canonical(original.RootPath);
        var operationId = Guid.NewGuid();
        var destination = UniqueManagedRoot(original.Name, original.Id);
        var staging = Path.Combine(paths.ManagedServers, ".chunkpilot-staging", operationId.ToString("N"));
        var journal = new ManagedCopyJournal(original, originalRoot, staging, destination, false,
            "Preparing a verified managed copy. The original is read-only and remains untouched.");
        await WriteManagedCopyJournalAsync(operationId, InstallState.Staging, journal, cancellationToken)
            .ConfigureAwait(false);

        ManagedInstanceCopyResult copied;
        try
        {
            copied = await managedCopies.MaterializeAsync(originalRoot, staging, destination, operationId,
                original.Id, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            journal = journal with { Activated = true, DisplayDetail = "The verified copy is being registered." };
            await WriteManagedCopyJournalAsync(operationId, InstallState.Registering, journal, CancellationToken.None)
                .ConfigureAwait(false);

            var updated = RewriteDefinitionForCopy(original, originalRoot, destination);
            await supervisor.ImportAsync(updated, cancellationToken).ConfigureAwait(false);
            await store.RecordInstanceHistoryAsync(original.Id, "ManagedCopy", originalRoot, copied.Sha256,
                $"Verified {copied.FileCount:N0} files ({copied.ByteCount:N0} bytes) for managed-copy destination {destination}. The source remained unchanged.",
                cancellationToken).ConfigureAwait(false);
            await store.CompleteOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            try { File.Delete(CreationOwnershipMarker.PathIn(destination)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return new ManagedCopyConversionReceipt
            {
                ServerId = original.Id,
                OriginalRoot = originalRoot,
                ManagedRoot = destination,
                CopiedBytes = copied.ByteCount,
                CopiedFiles = copied.FileCount,
                Detail = "ChunkPilot now manages the verified copy. The original folder was not modified and remains outside ChunkPilot ownership."
            };
        }
        catch (Exception failure)
        {
            try
            {
                var registered = (await store.GetServersAsync(CancellationToken.None).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Id == original.Id);
                if (registered is not null && SamePath(registered.RootPath, destination))
                    await supervisor.ImportAsync(original, CancellationToken.None).ConfigureAwait(false);
                if (Directory.Exists(destination))
                    ManagedInstanceCopyService.DeleteOperationOwnedCandidate(destination, operationId, original.Id);
                if (Directory.Exists(staging))
                    ManagedInstanceCopyService.DeleteOperationOwnedCandidate(staging, operationId, original.Id);
                await store.CompleteOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback)
            {
                await store.UpsertOperationAsync(operationId, "ManagedOwnershipCopy", InstallState.RecoveryRequired,
                    destination, staging, JsonSerializer.Serialize(journal with
                    {
                        DisplayDetail = $"Managed-copy rollback needs recovery: {rollback.Message}"
                    }, ProtocolJson.Options), CancellationToken.None).ConfigureAwait(false);
                throw new AggregateException("Managed copy failed and automatic rollback could not be proven complete.", failure, rollback);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ReconcileManagedOwnershipAsync(
        CancellationToken cancellationToken = default)
    {
        var reports = new List<string>();
        foreach (var definition in supervisor.Definitions.Where(item => item.IsManaged))
        {
            var before = ManagedInstanceOwnershipMarker.Inspect(definition.RootPath, definition.Id);
            var assessment = await AssessOwnershipAsync(definition, reconcileExactCreationEvidence: true,
                cancellationToken).ConfigureAwait(false);
            if (!before.Proven && assessment.Status == ManagedOwnershipStatus.ReconciledCreationEvidence)
                reports.Add($"Reconciled exact creation ownership for {definition.Name} ({definition.Id:D}).");
        }
        return reports;
    }

    /// <summary>
    /// Reconciles only deletion operations created by this coordinator. If the registration still
    /// exists, data is restored to its original paths. If metadata was already removed, Recovery is
    /// retained (or a fully acknowledged permanent deletion is finished) without recreating state.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var reports = new List<string>();
        var definitions = (await store.GetServersAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.Id);
        foreach (var operation in (await store.GetInterruptedOperationsAsync(cancellationToken).ConfigureAwait(false))
                     .Where(item => item.Type.Equals("ManagedOwnershipCopy", StringComparison.Ordinal)))
        {
            ManagedCopyJournal? journal;
            try { journal = JsonSerializer.Deserialize<ManagedCopyJournal>(operation.Detail, ProtocolJson.Options); }
            catch (JsonException) { journal = null; }
            if (journal is null || journal.OriginalDefinition.Id == Guid.Empty)
            {
                reports.Add($"Managed copy {operation.Id:D} needs manual recovery because its journal is unreadable.");
                continue;
            }
            if (definitions.TryGetValue(journal.OriginalDefinition.Id, out var registered) &&
                SamePath(registered.RootPath, journal.Destination) &&
                ManagedInstanceOwnershipMarker.Proves(journal.Destination, registered.Id))
            {
                await store.CompleteOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
                try { File.Delete(CreationOwnershipMarker.PathIn(journal.Destination)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                reports.Add($"Finished registration of the interrupted managed copy for {registered.Name}.");
                continue;
            }
            if (definitions.TryGetValue(journal.OriginalDefinition.Id, out registered) &&
                SamePath(registered.RootPath, journal.OriginalRoot))
            {
                try
                {
                    if (Directory.Exists(journal.Destination))
                        ManagedInstanceCopyService.DeleteOperationOwnedCandidate(journal.Destination, operation.Id, registered.Id);
                    if (Directory.Exists(journal.Staging))
                        ManagedInstanceCopyService.DeleteOperationOwnedCandidate(journal.Staging, operation.Id, registered.Id);
                    await store.CompleteOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
                    reports.Add($"Rolled back the interrupted managed copy for {registered.Name}; its source was untouched.");
                    continue;
                }
                catch (Exception exception)
                {
                    await store.UpsertOperationAsync(operation.Id, "ManagedOwnershipCopy", InstallState.RecoveryRequired,
                        journal.Destination, journal.Staging, JsonSerializer.Serialize(journal with
                        { DisplayDetail = exception.Message }, ProtocolJson.Options), cancellationToken).ConfigureAwait(false);
                }
            }
            reports.Add($"Managed copy {operation.Id:D} needs manual recovery; no ownership-uncertain path was changed.");
        }

        var registeredIds = definitions.Keys.ToHashSet();
        foreach (var operation in (await store.GetInterruptedOperationsAsync(cancellationToken).ConfigureAwait(false))
                     .Where(item => item.Type.Equals("ServerDeletion", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServerDeletionJournal? journal;
            try { journal = JsonSerializer.Deserialize<ServerDeletionJournal>(operation.Detail, ProtocolJson.Options); }
            catch (JsonException) { journal = null; }
            if (journal is null || journal.ServerId == Guid.Empty)
            {
                await store.UpsertOperationAsync(operation.Id, "ServerDeletion", InstallState.RecoveryRequired,
                    operation.Target, operation.Staging, operation.Detail, cancellationToken).ConfigureAwait(false);
                reports.Add($"Deletion {operation.Id:D} needs manual recovery because its journal is unreadable.");
                continue;
            }

            if (registeredIds.Contains(journal.ServerId))
            {
                foreach (var moved in journal.MovedBackups.Reverse())
                    if (File.Exists(moved.Staged) && !File.Exists(moved.Original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(moved.Original)!);
                        File.Move(moved.Staged, moved.Original);
                    }
                if (Directory.Exists(journal.StagedServer) && !Directory.Exists(journal.OriginalRoot))
                    Directory.Move(journal.StagedServer, journal.OriginalRoot);
                if (!Directory.Exists(journal.OriginalRoot))
                {
                    await store.UpsertOperationAsync(operation.Id, "ServerDeletion", InstallState.RecoveryRequired,
                        journal.OriginalRoot, journal.StagedServer, operation.Detail, cancellationToken).ConfigureAwait(false);
                    reports.Add($"Deletion {operation.Id:D} needs manual recovery because the registered server root is missing.");
                    continue;
                }
                reports.Add($"Restored the interrupted deletion of {journal.OriginalRoot}.");
            }
            else if (journal.Mode == ServerDeletionMode.Permanent)
            {
                DeleteTreeWithoutFollowingReparsePoints(journal.StagedServer);
                foreach (var moved in journal.MovedBackups)
                    if (File.Exists(moved.Staged) && !File.GetAttributes(moved.Staged).HasFlag(FileAttributes.ReparsePoint))
                        File.Delete(moved.Staged);
                reports.Add($"Finished the interrupted permanent deletion for {journal.ServerId:D}.");
            }
            else
            {
                reports.Add($"Retained the interrupted deletion for {journal.ServerId:D} in Recovery.");
            }
            await store.CompleteOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        }
        return reports;
    }

    private async Task<OwnershipAssessment> AssessOwnershipAsync(ServerDefinition definition,
        bool reconcileExactCreationEvidence, CancellationToken cancellationToken)
    {
        var evidence = new List<ManagedOwnershipEvidence>();
        if (!definition.IsManaged)
            return new(false, ManagedOwnershipStatus.External, "Imported/by-reference data is not owned by ChunkPilot.",
                [new("managed-registration", false, "The server is registered by reference, not as managed data.")]);
        var root = Canonical(definition.RootPath);
        var declaredRoot = string.IsNullOrWhiteSpace(definition.ManagedInstanceRoot)
            ? "" : Canonical(definition.ManagedInstanceRoot);
        var rootExists = Directory.Exists(root);
        var insideDeclared = rootExists && !string.IsNullOrWhiteSpace(declaredRoot) &&
                             IsUnder(declaredRoot, root) && !SamePath(declaredRoot, root);
        var unique = supervisor.Definitions.Count(item => SamePath(item.RootPath, root)) == 1;
        var noReparse = rootExists && insideDeclared && !HasReparsePoint(root, declaredRoot);
        evidence.Add(new("managed-registration", true, "The registration identifies this server as managed."));
        evidence.Add(new("registered-root", insideDeclared, insideDeclared
            ? "The exact registered root is a child of its declared managed-instance root."
            : "The registered root does not exactly agree with its declared managed-instance root."));
        evidence.Add(new("unique-root", unique, unique
            ? "No other registered server uses this root." : "Another registered server uses this root."));
        evidence.Add(new("closed-boundary", noReparse, noReparse
            ? "The root ancestry has no reparse-point boundary." : "The root boundary is missing or ownership-uncertain."));
        var marker = ManagedInstanceOwnershipMarker.Inspect(root, definition.Id);
        evidence.Add(new("persistent-marker", marker.Proven, marker.Detail));
        if (insideDeclared && unique && noReparse && marker.Proven)
        {
            var status = marker.Marker?.OwnershipSource.Equals("ReconciledCreationEvidence", StringComparison.Ordinal) == true
                ? ManagedOwnershipStatus.ReconciledCreationEvidence : ManagedOwnershipStatus.ProvenMarker;
            return new(true, status, marker.Detail, evidence);
        }

        var install = await store.GetManagedInstallEvidenceAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        var exactHistory = install is not null && install.Sha256.Length == 64 &&
                           install.Sha256.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(install.Source);
        evidence.Add(new("creation-transaction", exactHistory, exactHistory
            ? $"A successful install transaction recorded exact artifact SHA-256 {install!.Sha256}."
            : "No successful creation transaction with exact artifact integrity is recorded."));
        var inDefaultRoot = insideDeclared && SamePath(declaredRoot, paths.ManagedServers);
        if (reconcileExactCreationEvidence && ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
                definition.IsManaged, inDefaultRoot, unique, noReparse, marker.MarkerPresent, exactHistory))
        {
            await ManagedInstanceOwnershipMarker.WriteAsync(root, definition.Id, cancellationToken,
                "ReconciledCreationEvidence", install!.Sha256).ConfigureAwait(false);
            evidence[3] = new("persistent-marker", true,
                "A marker was restored from exact creation, registration, path, and integrity evidence.");
            return new(true, ManagedOwnershipStatus.ReconciledCreationEvidence,
                "Persistent ownership was restored from exact successful-creation evidence.", evidence);
        }
        return new(false, ManagedOwnershipStatus.Ambiguous,
            marker.MarkerPresent
                ? marker.Detail
                : "No exact marker or complete creation evidence proves ChunkPilot owns this folder.", evidence);
    }

    private bool CanCreateManagedCopy(ServerDefinition definition, ServerState state, string sourceRoot)
    {
        if (!definition.IsManaged || state is not ServerState.Stopped and not ServerState.Crashed ||
            !Directory.Exists(sourceRoot) || SamePath(sourceRoot, paths.ManagedServers) ||
            SamePath(sourceRoot, paths.Root) || IsUnder(sourceRoot, paths.ManagedServers) ||
            IsUnder(sourceRoot, paths.Root))
            return false;
        return !supervisor.Definitions.Any(item => item.Id != definition.Id &&
            (SamePath(sourceRoot, item.RootPath) || IsUnder(sourceRoot, item.RootPath)));
    }

    private void RemoveExpired()
    {
        foreach (var item in preflights.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
            preflights.TryRemove(item.Key, out _);
    }

    private static string ResolveWorldPath(string root)
    {
        var levelName = "world";
        var properties = Path.Combine(root, "server.properties");
        try
        {
            if (File.Exists(properties))
            {
                var line = File.ReadLines(properties).FirstOrDefault(value =>
                    value.TrimStart().StartsWith("level-name=", StringComparison.OrdinalIgnoreCase));
                if (line is not null) levelName = line[(line.IndexOf('=') + 1)..].Trim();
            }
        }
        catch (IOException) { }
        if (string.IsNullOrWhiteSpace(levelName)) levelName = "world";
        return Canonical(Path.IsPathRooted(levelName) ? levelName : Path.Combine(root, levelName));
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string root)
    {
        if (!Directory.Exists(root)) return;
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Permanent deletion refused a reparse-point server root.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Permanent deletion refused reparse-point content: {Path.GetFileName(entry)}");
            if (attributes.HasFlag(FileAttributes.Directory)) DeleteTreeWithoutFollowingReparsePoints(entry);
            else File.Delete(entry);
        }
        Directory.Delete(root, recursive: false);
    }

    private static string? FindReparsePoint(string root, int maximumEntries = 250_000)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var seen = 0;
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++seen > maximumEntries)
                    throw new InvalidOperationException(
                        $"Permanent deletion refused to inspect more than {maximumEntries:N0} entries. Move the server to Recovery instead.");
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return entry;
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
            }
        }
        return null;
    }

    private Task WriteJournalAsync(Guid operationId, InstallState state, ServerDeletionJournal journal,
        string displayDetail, CancellationToken cancellationToken) => store.UpsertOperationAsync(
        operationId, "ServerDeletion", state, journal.OriginalRoot, journal.StagedServer,
        JsonSerializer.Serialize(journal with { DisplayDetail = displayDetail }, ProtocolJson.Options), cancellationToken);

    private Task WriteManagedCopyJournalAsync(Guid operationId, InstallState state,
        ManagedCopyJournal journal, CancellationToken cancellationToken) => store.UpsertOperationAsync(
        operationId, "ManagedOwnershipCopy", state, journal.Destination, journal.Staging,
        JsonSerializer.Serialize(journal, ProtocolJson.Options), cancellationToken);

    private string UniqueManagedRoot(string name, Guid serverId)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var slug = new string(name.Trim().Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        slug = string.Join('-', slug.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)).Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "Managed-Server";
        if (slug.Length > 48) slug = slug[..48].TrimEnd();
        var prefix = $"{slug}-{serverId:N}"[..Math.Min(slug.Length + 9, slug.Length + 33)];
        var candidate = Path.Combine(paths.ManagedServers, prefix);
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = Path.Combine(paths.ManagedServers, $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, slug.Length + 33)]);
        return Canonical(candidate);
    }

    private static ServerDefinition RewriteDefinitionForCopy(ServerDefinition definition, string source, string destination)
    {
        string Remap(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            try
            {
                var canonical = Path.IsPathRooted(value) ? Canonical(value) : value;
                if (Path.IsPathRooted(value) && (SamePath(canonical, source) || IsUnder(source, canonical)))
                    return Path.Combine(destination, Path.GetRelativePath(source, canonical));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
            return value.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
        }

        return definition with
        {
            RootPath = destination,
            WorkingDirectory = Remap(definition.WorkingDirectory),
            Executable = Remap(definition.Executable),
            Arguments = definition.Arguments.Replace(source, destination, StringComparison.OrdinalIgnoreCase),
            Environment = definition.Environment.ToDictionary(item => item.Key, item => Remap(item.Value),
                StringComparer.OrdinalIgnoreCase),
            IsManaged = true,
            ManagedInstanceRoot = Path.GetDirectoryName(destination)!
        };
    }

    private static string Fingerprint(ServerDeletionPreflight value)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            value.ServerId, value.ServerName, value.State, value.IsManaged, value.OwnershipProven,
            value.OwnershipStatus, value.ManagedRoot, value.WorldLocation, value.BackupCount,
            value.ManagedBackupPaths, value.ProtectedExternalPaths, value.ActiveScheduleCount,
            value.InternetSharingConfigured, value.FirewallRemovalRequired, value.Blockers
        }, ProtocolJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool HasReparsePoint(string path, string stopAt)
    {
        for (var current = new DirectoryInfo(path); current is not null && !SamePath(current.FullName, stopAt); current = current.Parent)
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static string UniquePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        return File.Exists(candidate) ? Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + name) : candidate;
    }

    private static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool SamePath(string left, string right) => Canonical(left).Equals(Canonical(right), StringComparison.OrdinalIgnoreCase);
    private static bool IsUnder(string root, string path) => Canonical(path).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record ServerDeletionJournal(
        Guid ServerId,
        ServerDeletionMode Mode,
        string OriginalRoot,
        string StagedServer,
        string RecoveryRoot,
        IReadOnlyList<MovedPath> MovedBackups,
        string DisplayDetail = "");

    private sealed record ManagedCopyJournal(
        ServerDefinition OriginalDefinition,
        string OriginalRoot,
        string Staging,
        string Destination,
        bool Activated,
        string DisplayDetail);

    private sealed record OwnershipAssessment(
        bool Proven,
        ManagedOwnershipStatus Status,
        string Detail,
        IReadOnlyList<ManagedOwnershipEvidence> Evidence);

    private sealed record MovedPath(string Original, string Staged);
}
