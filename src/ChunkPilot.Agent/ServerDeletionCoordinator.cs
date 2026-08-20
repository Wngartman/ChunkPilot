using System.Collections.Concurrent;
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
    WindowsFirewallCoordinator firewallAccess)
{
    private readonly ConcurrentDictionary<Guid, ServerDeletionPreflight> preflights = new();

    public async Task<ServerDeletionPreflight> PreflightAsync(
        Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = supervisor.Get(serverId);
        var definition = server.Definition;
        var root = Canonical(definition.RootPath);
        var managedRoot = Canonical(paths.ManagedServers);
        var rootInsideManaged = IsUnder(managedRoot, root) && !SamePath(managedRoot, root);
        var ownership = definition.IsManaged && rootInsideManaged &&
                        SamePath(definition.ManagedInstanceRoot, paths.ManagedServers) &&
                        !HasReparsePoint(root, stopAt: managedRoot) &&
                        ManagedInstanceOwnershipMarker.Proves(root, serverId);
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
            blockers.Add("ChunkPilot cannot prove durable ownership of this managed folder. Data deletion is disabled; removal from ChunkPilot remains available.");
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
        if (!string.Equals(current.ServerName, prior.ServerName, StringComparison.Ordinal) || current.State != prior.State ||
            current.OwnershipProven != prior.OwnershipProven)
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

    /// <summary>
    /// Reconciles only deletion operations created by this coordinator. If the registration still
    /// exists, data is restored to its original paths. If metadata was already removed, Recovery is
    /// retained (or a fully acknowledged permanent deletion is finished) without recreating state.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var reports = new List<string>();
        var registered = (await store.GetServersAsync(cancellationToken).ConfigureAwait(false))
            .Select(item => item.Id).ToHashSet();
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

            if (registered.Contains(journal.ServerId))
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

    private sealed record MovedPath(string Original, string Staged);
}
