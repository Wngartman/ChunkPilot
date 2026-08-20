using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed class SchedulerWorker
{
    private readonly ChunkPilotStore store;
    private readonly ServerSupervisor supervisor;
    private readonly BackupService backupService;
    private readonly ServerUpdateCoordinator updates;
    private readonly ConcurrentDictionary<Guid, byte> running = new();
    private readonly ConcurrentDictionary<Guid, byte> automaticUpdates = new();
    private readonly ILogger<SchedulerWorker> logger;
    private DateTimeOffset lastUpdateSweep = DateTimeOffset.MinValue;
    private DateTimeOffset lastSnapshotCleanup = DateTimeOffset.MinValue;

    public SchedulerWorker(
        ChunkPilotStore store,
        ServerSupervisor supervisor,
        BackupService backupService,
        ServerUpdateCoordinator updates,
        ILogger<SchedulerWorker> logger)
    {
        this.store = store;
        this.supervisor = supervisor;
        this.backupService = backupService;
        this.updates = updates;
        this.logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.Now;
            foreach (var schedule in await store.GetSchedulesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!schedule.Enabled || schedule.NextRunAt is not { } next || next > now)
                    continue;
                if (!schedule.AllowOverlap && !running.TryAdd(schedule.Id, 0))
                    continue;
                _ = ExecuteAndRescheduleAsync(schedule, cancellationToken);
            }
            if (now - lastUpdateSweep >= TimeSpan.FromMinutes(1))
            {
                lastUpdateSweep = now;
                await SweepAutomaticUpdatesAsync(now, cancellationToken).ConfigureAwait(false);
            }
            if (now - lastSnapshotCleanup >= TimeSpan.FromHours(24))
            {
                lastSnapshotCleanup = now;
                await CleanupExpiredSnapshotsAsync(now, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupExpiredSnapshotsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var definition in supervisor.Definitions)
        {
            try
            {
                var versions = await store.GetVersionSnapshotsAsync(definition.Id, cancellationToken)
                    .ConfigureAwait(false);
                var active = versions.FirstOrDefault(item => item.IsActive);
                if (active?.Health != VersionHealth.Healthy)
                    continue;
                var backups = await store.GetBackupsAsync(definition.Id, cancellationToken).ConfigureAwait(false);
                if (!backups.Any(item => item.Verified))
                    continue;
                foreach (var expired in versions.Where(item =>
                             !item.IsActive && !item.KeepPermanently &&
                             item.RetainUntil is { } retainUntil && retainUntil <= now).ToArray())
                {
                    await updates.DeleteVersionAsync(definition.Id, expired.Id, cancellationToken)
                        .ConfigureAwait(false);
                    versions = await store.GetVersionSnapshotsAsync(definition.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Expired snapshot cleanup skipped for {Server}", definition.Name);
            }
        }
    }

    private async Task SweepAutomaticUpdatesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var definition in supervisor.Definitions)
        {
            try
            {
                var preferences = await store.GetUpdatePreferencesAsync(definition.Id, cancellationToken)
                    .ConfigureAwait(false);
                var check = await store.GetLatestUpdateCheckAsync(definition.Id, cancellationToken)
                    .ConfigureAwait(false);
                var interval = TimeSpan.FromHours(Math.Clamp(preferences.CheckIntervalHours, 1, 24 * 30));
                if (preferences.AutomaticChecksEnabled &&
                    (check is null || now - check.CheckedAt >= interval))
                    check = await updates.CheckAsync(definition.Id, cancellationToken).ConfigureAwait(false);
                if (check?.Status != ServerUpdateStatus.UpdateAvailable ||
                    check.LatestVersion is null ||
                    !preferences.AutomaticInstallEnabled ||
                    !IsMaintenanceWindow(now.TimeOfDay, preferences.MaintenanceWindow) ||
                    automaticUpdates.ContainsKey(definition.Id))
                    continue;

                var blockers = UpdatePolicy.ValidateAutomaticInstall(check, preferences).ToList();
                var managed = supervisor.Get(definition.Id);
                var snapshot = managed.Snapshot();
                if (managed.State == ServerState.Running && snapshot.OnlinePlayers is not 0)
                    blockers.Add(snapshot.OnlinePlayers is null
                        ? "Player status is unknown; unattended update skipped."
                        : $"{snapshot.OnlinePlayers} player(s) are online.");
                if (managed.State is not ServerState.Running and not ServerState.Stopped and not ServerState.Crashed)
                    blockers.Add($"Server state {managed.State} is not safe for an unattended update.");
                if (blockers.Count > 0 || !automaticUpdates.TryAdd(definition.Id, 0))
                    continue;

                var attemptKey = $"automatic-update-attempt:{definition.Id:D}";
                var attemptedVersion = await store.GetSettingAsync(attemptKey, cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(attemptedVersion, check.LatestVersion.VersionId, StringComparison.Ordinal))
                {
                    automaticUpdates.TryRemove(definition.Id, out _);
                    continue;
                }
                var backup = await supervisor.BackupAsync(
                    definition.Id, "Automatic pre-update", cancellationToken).ConfigureAwait(false);
                if (!backup.Verified)
                {
                    automaticUpdates.TryRemove(definition.Id, out _);
                    logger.LogWarning("Automatic update skipped for {Server}: pre-update backup was not verified.",
                        definition.Name);
                    continue;
                }
                await store.SetSettingAsync(attemptKey, check.LatestVersion.VersionId, cancellationToken)
                    .ConfigureAwait(false);
                var operationId = updates.Begin(new UpdateInstallRequest
                {
                    ServerId = definition.Id,
                    TargetVersion = check.LatestVersion,
                    PlayerCountdownSeconds = 60,
                    StartForValidation = true,
                    Automatic = true,
                    ConfirmedMigrationWarnings = false
                });
                _ = MonitorAutomaticUpdateAsync(definition.Id, operationId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Automatic update sweep failed for {Server}", definition.Name);
            }
        }
    }

    private async Task MonitorAutomaticUpdateAsync(
        Guid serverId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = updates.Get(operationId);
                if (state.IsTerminal)
                {
                    if (!state.Success)
                        logger.LogWarning("Automatic update {Operation} ended safely without activation: {Error}",
                            operationId, state.Error);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            automaticUpdates.TryRemove(serverId, out _);
        }
    }

    private static bool IsMaintenanceWindow(TimeSpan now, TimeSpan start)
    {
        var normalized = TimeSpan.FromTicks(
            ((start.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
        var end = normalized.Add(TimeSpan.FromMinutes(30));
        return end < TimeSpan.FromDays(1)
            ? now >= normalized && now < end
            : now >= normalized || now < end - TimeSpan.FromDays(1);
    }

    private async Task ExecuteAndRescheduleAsync(ScheduleEntry schedule, CancellationToken cancellationToken)
    {
        try
        {
            var server = supervisor.Get(schedule.ServerId);
            OperationResult result = schedule.Action switch
            {
                ScheduledAction.Start => await server.StartAsync("Scheduled", cancellationToken).ConfigureAwait(false),
                ScheduledAction.Save => await server.SaveAsync("Scheduled", cancellationToken).ConfigureAwait(false),
                ScheduledAction.Stop => await server.StopAsync(true, "Scheduled", cancellationToken).ConfigureAwait(false),
                ScheduledAction.Restart => await SafeScheduledRestartAsync(server, schedule, cancellationToken).ConfigureAwait(false),
                ScheduledAction.Backup => ToResult(await supervisor.BackupAsync(schedule.ServerId, "Scheduled", cancellationToken).ConfigureAwait(false)),
                ScheduledAction.SendCommand => await server.SendCommandAsync(schedule.Command, "Scheduled", cancellationToken).ConfigureAwait(false),
                ScheduledAction.VerifyBackups => await VerifyBackupsAsync(schedule.ServerId, cancellationToken).ConfigureAwait(false),
                ScheduledAction.DeleteOldLogs => DeleteOldLogs(server.Definition.RootPath),
                _ => OperationResult.Fail("Unsupported scheduled action.")
            };
            if (!result.Success)
                logger.LogWarning("Scheduled task {Task} failed: {Message}", schedule.Name, result.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Scheduled task {Task} failed", schedule.Name);
        }
        finally
        {
            var updated = schedule with
            {
                LastRunAt = DateTimeOffset.Now,
                NextRunAt = null,
                Enabled = schedule.Kind != ScheduleKind.OneTime
            };
            updated = updated with { NextRunAt = ScheduleCalculator.NextRun(updated, DateTimeOffset.Now) };
            await store.UpsertScheduleAsync(updated, CancellationToken.None).ConfigureAwait(false);
            running.TryRemove(schedule.Id, out _);
        }
    }

    private async Task<OperationResult> VerifyBackupsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var records = await store.GetBackupsAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
            return OperationResult.Fail("No backups exist to verify.");
        var failures = 0;
        foreach (var record in records)
        {
            if (!await backupService.VerifyAsync(record, cancellationToken).ConfigureAwait(false))
                failures++;
        }
        return failures == 0
            ? OperationResult.Ok($"Verified {records.Count} backup archive(s).")
            : OperationResult.Fail($"{failures} of {records.Count} backup archive(s) failed verification.");
    }

    private async Task<OperationResult> SafeScheduledRestartAsync(
        ManagedServer server,
        ScheduleEntry schedule,
        CancellationToken cancellationToken)
    {
        if (server.State != ServerState.Running)
            return OperationResult.Fail($"Automatic restart skipped because the server is {server.State}.");
        var countdown = Math.Clamp(schedule.RestartCountdownSeconds, 0, 3_600);
        if (countdown > 0)
        {
            var warning = await server.SendCommandAsync(
                $"say Server restarting safely in {countdown} seconds.", "Scheduled", cancellationToken).ConfigureAwait(false);
            if (!warning.Success)
                return warning;
            await Task.Delay(TimeSpan.FromSeconds(countdown), cancellationToken).ConfigureAwait(false);
        }
        if (schedule.BackupBeforeRestart)
            _ = await supervisor.BackupAsync(server.Definition.Id, "Scheduled pre-restart", cancellationToken).ConfigureAwait(false);
        return await server.RestartAsync("Scheduled", cancellationToken).ConfigureAwait(false);
    }

    private static OperationResult DeleteOldLogs(string root)
    {
        var logs = Path.Combine(root, "logs");
        if (!Directory.Exists(logs))
            return OperationResult.Ok("No logs directory exists.");
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(logs, "*.log.gz", SearchOption.TopDirectoryOnly)
                     .Where(file => File.GetLastWriteTimeUtc(file) < cutoff))
        {
            File.Delete(file);
            deleted++;
        }
        return OperationResult.Ok($"Deleted {deleted} compressed log file(s) older than 30 days.");
    }

    private static OperationResult ToResult(BackupRecord backup) =>
        OperationResult.Ok($"Backup created and {(backup.Verified ? "verified" : "not verified")}.", backup.ArchivePath);
}
