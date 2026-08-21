using System.Text.Json;
using ChunkPilot.Core;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.Infrastructure;

public sealed class ChunkPilotStore : IAsyncDisposable
{
    private readonly AppDataPaths paths;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string connectionString;

    public ChunkPilotStore(AppDataPaths paths)
    {
        this.paths = paths;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;",
                cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS servers (
                    id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS activity (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    server_id TEXT NULL,
                    server_name TEXT NOT NULL,
                    action TEXT NOT NULL,
                    result TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    error TEXT NOT NULL,
                    source TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_activity_timestamp ON activity(timestamp_utc DESC);
                CREATE TABLE IF NOT EXISTS backups (
                    id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_backups_server ON backups(server_id, created_utc DESC);
                CREATE TABLE IF NOT EXISTS schedules (
                    id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    next_run_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_schedules_next ON schedules(next_run_utc);
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS statistics_hourly (
                    server_id TEXT NOT NULL,
                    hour_utc TEXT NOT NULL,
                    cpu_average REAL NOT NULL,
                    ram_average INTEGER NOT NULL,
                    sample_count INTEGER NOT NULL,
                    PRIMARY KEY(server_id, hour_utc)
                );
                CREATE TABLE IF NOT EXISTS operation_journal (
                    operation_id TEXT PRIMARY KEY,
                    operation_type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    target_path TEXT NOT NULL,
                    staging_path TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS eula_acceptance (
                    server_id TEXT PRIMARY KEY,
                    accepted_utc TEXT NOT NULL,
                    eula_url TEXT NOT NULL,
                    reference TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS instance_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    server_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    action TEXT NOT NULL,
                    source TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    detail TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS plugin_manifests (
                    server_id TEXT NOT NULL,
                    plugin_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    PRIMARY KEY(server_id, plugin_id)
                );
                CREATE TABLE IF NOT EXISTS update_sources (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS update_checks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    server_id TEXT NOT NULL,
                    checked_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_update_checks_server
                    ON update_checks(server_id, checked_utc DESC);
                CREATE TABLE IF NOT EXISTS version_snapshots (
                    id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    installed_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_version_snapshots_server
                    ON version_snapshots(server_id, installed_utc DESC);
                CREATE TABLE IF NOT EXISTS migration_decisions (
                    operation_id TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    json TEXT NOT NULL,
                    PRIMARY KEY(operation_id, relative_path)
                );
                CREATE TABLE IF NOT EXISTS update_downloads (
                    operation_id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    version_id TEXT NOT NULL,
                    source_url TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    provider_hash TEXT NOT NULL,
                    status TEXT NOT NULL,
                    completed_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_update_downloads_server
                    ON update_downloads(server_id, completed_utc DESC);
                CREATE TABLE IF NOT EXISTS update_preferences (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS rollback_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    server_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    from_version TEXT NOT NULL,
                    to_version TEXT NOT NULL,
                    result TEXT NOT NULL,
                    detail TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS capability_profiles (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS quick_start_presets (
                    id TEXT PRIMARY KEY,
                    server_id TEXT NULL,
                    json TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_key TEXT NOT NULL,
                    action TEXT NOT NULL,
                    json TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_favorites (
                    project_key TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS managed_java_runtimes (
                    id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    installed_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS java_assignments (
                    server_id TEXT PRIMARY KEY,
                    runtime_id TEXT NULL,
                    java_path TEXT NOT NULL,
                    source TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS network_configurations (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS router_mappings (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS firewall_access (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS tunnel_providers (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS crossplay_configurations (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS global_access_rules (
                    id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS gamerule_profiles (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS datapack_inventory (
                    server_id TEXT NOT NULL,
                    item_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY(server_id, item_id)
                );
                CREATE TABLE IF NOT EXISTS resource_pack_configurations (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS automation_recipes (
                    id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS share_settings (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS diagnostics_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    server_id TEXT NULL,
                    json TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS process_identities (
                    server_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS application_sessions (
                    session_id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    heartbeat_utc TEXT NOT NULL,
                    closed_utc TEXT NULL,
                    exit_kind TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS server_running_state (
                    server_id TEXT PRIMARY KEY,
                    autostart_mode TEXT NOT NULL,
                    was_running INTEGER NOT NULL,
                    last_intent TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS file_operation_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_path TEXT NOT NULL,
                    operation_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS creation_journal (
                    operation_id TEXT PRIMARY KEY,
                    server_id TEXT NOT NULL,
                    destination TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    schema_version INTEGER NOT NULL,
                    json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_creation_journal_destination
                    ON creation_journal(destination COLLATE NOCASE);
                PRAGMA user_version=6;
                """, cancellationToken, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerDefinition>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM servers ORDER BY json_extract(json, '$.name') COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ServerDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var server = JsonSerializer.Deserialize<ServerDefinition>(reader.GetString(0), ProtocolJson.Options);
            if (server is not null)
                results.Add(server);
        }
        return results;
    }

    public async Task UpsertServerAsync(ServerDefinition server, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO servers(id, json, updated_utc) VALUES($id, $json, $utc)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", server.Id.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(server, ProtocolJson.Options));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteServerAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Keep activity as an audit tombstone. Every other server-owned row is removed in the same
        // transaction so a deleted registration cannot leave actionable schedules, networking intent,
        // process identity, or stale management state behind.
        foreach (var table in new[]
                 {
                     "schedules", "backups", "statistics_hourly", "eula_acceptance", "instance_history",
                     "plugin_manifests", "update_sources", "update_checks", "version_snapshots",
                     "update_downloads", "update_preferences", "rollback_history", "capability_profiles",
                     "java_assignments", "network_configurations", "router_mappings", "firewall_access",
                     "tunnel_providers", "crossplay_configurations", "gamerule_profiles", "datapack_inventory",
                     "resource_pack_configurations", "automation_recipes", "share_settings", "diagnostics_history",
                     "process_identities", "server_running_state", "creation_journal"
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE server_id=$id";
            command.Parameters.AddWithValue("$id", serverId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var presets = connection.CreateCommand())
        {
            presets.Transaction = (SqliteTransaction)transaction;
            presets.CommandText = "DELETE FROM quick_start_presets WHERE server_id=$id";
            presets.Parameters.AddWithValue("$id", serverId.ToString("D"));
            await presets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = (SqliteTransaction)transaction;
            settings.CommandText = "DELETE FROM settings WHERE key IN ($pending, $automatic)";
            settings.Parameters.AddWithValue("$pending", $"pending-gamerules:{serverId:D}");
            settings.Parameters.AddWithValue("$automatic", $"automatic-update-attempt:{serverId:D}");
            await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var server = connection.CreateCommand())
        {
            server.Transaction = (SqliteTransaction)transaction;
            server.CommandText = "DELETE FROM servers WHERE id=$id";
            server.Parameters.AddWithValue("$id", serverId.ToString("D"));
            await server.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddActivityAsync(ActivityEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity(timestamp_utc, server_id, server_name, action, result, duration_ms, error, source)
            VALUES($timestamp, $serverId, $serverName, $action, $result, $duration, $error, $source)
            """;
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$serverId", (object?)entry.ServerId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$serverName", entry.ServerName);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$result", entry.Result);
        command.Parameters.AddWithValue("$duration", entry.DurationMilliseconds);
        command.Parameters.AddWithValue("$error", entry.Error);
        command.Parameters.AddWithValue("$source", entry.Source);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordCrashAnalysisAsync(
        CrashAnalysisReport report,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO diagnostics_history(server_id, json, timestamp_utc)
                VALUES($server, $json, $timestamp)
                """;
            insert.Parameters.AddWithValue("$server", report.ServerId.ToString("D"));
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report, ProtocolJson.Options));
            insert.Parameters.AddWithValue("$timestamp", report.AnalyzedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = """
                DELETE FROM diagnostics_history
                WHERE server_id=$server AND id NOT IN (
                    SELECT id FROM diagnostics_history
                    WHERE server_id=$server ORDER BY id DESC LIMIT 20)
                """;
            prune.Parameters.AddWithValue("$server", report.ServerId.ToString("D"));
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrashAnalysisReport?> GetLatestCrashAnalysisAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json FROM diagnostics_history
            WHERE server_id=$server ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CrashAnalysisReport>(json, ProtocolJson.Options);
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, server_id, server_name, action, result, duration_ms, error, source
            FROM activity ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ActivityEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ActivityEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                ServerId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                ServerName = reader.GetString(3),
                Action = reader.GetString(4),
                Result = reader.GetString(5),
                DurationMilliseconds = reader.GetInt64(6),
                Error = reader.GetString(7),
                Source = reader.GetString(8)
            });
        }
        return results;
    }

    public async Task UpsertBackupAsync(BackupRecord backup, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backups(id, server_id, json, created_utc) VALUES($id, $server, $json, $created)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json
            """;
        command.Parameters.AddWithValue("$id", backup.Id.ToString("D"));
        command.Parameters.AddWithValue("$server", backup.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(backup, ProtocolJson.Options));
        command.Parameters.AddWithValue("$created", backup.CreatedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBackupRecordAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM backups WHERE id=$id";
        command.Parameters.AddWithValue("$id", backupId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetBackupsAsync(Guid? serverId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = serverId is null
            ? "SELECT json FROM backups ORDER BY created_utc DESC"
            : "SELECT json FROM backups WHERE server_id=$server ORDER BY created_utc DESC";
        if (serverId is not null)
            command.Parameters.AddWithValue("$server", serverId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<BackupRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var backup = JsonSerializer.Deserialize<BackupRecord>(reader.GetString(0), ProtocolJson.Options);
            if (backup is not null)
                results.Add(backup);
        }
        return results;
    }

    public async Task UpsertScheduleAsync(ScheduleEntry schedule, CancellationToken cancellationToken = default)
    {
        var next = ScheduleCalculator.NextRun(schedule, DateTimeOffset.Now);
        var saved = schedule with { NextRunAt = next };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO schedules(id, server_id, json, next_run_utc) VALUES($id, $server, $json, $next)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json, next_run_utc=excluded.next_run_utc
            """;
        command.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
        command.Parameters.AddWithValue("$server", saved.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved, ProtocolJson.Options));
        command.Parameters.AddWithValue("$next", (object?)next?.UtcDateTime.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleEntry>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM schedules ORDER BY next_run_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ScheduleEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schedule = JsonSerializer.Deserialize<ScheduleEntry>(reader.GetString(0), ProtocolJson.Options);
            if (schedule is not null)
                results.Add(schedule);
        }
        return results;
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schedules WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task UpsertOperationAsync(
        Guid operationId,
        string operationType,
        InstallState state,
        string targetPath,
        string stagingPath,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_journal(operation_id, operation_type, state, target_path, staging_path, detail, updated_utc)
            VALUES($id, $type, $state, $target, $staging, $detail, $updated)
            ON CONFLICT(operation_id) DO UPDATE SET
                state=excluded.state, target_path=excluded.target_path, staging_path=excluded.staging_path,
                detail=excluded.detail, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$type", operationType);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$target", targetPath);
        command.Parameters.AddWithValue("$staging", stagingPath);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operation_journal WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Guid Id, string Type, string State, string Target, string Staging, string Detail)>>
        GetInterruptedOperationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id, operation_type, state, target_path, staging_path, detail FROM operation_journal ORDER BY updated_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<(Guid, string, string, string, string, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return results;
    }

    /// <summary>
    /// Writes or replaces one creation-journal entry.
    /// </summary>
    /// <remarks>
    /// A single upsert statement, so the row a reader sees is either the old one or the new one and
    /// never a half-written mixture. The queryable columns are duplicated out of the payload so
    /// recovery and the destination policy can find an entry without deserialising every row.
    /// </remarks>
    public async Task UpsertCreationJournalAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creation_journal(operation_id, server_id, destination, phase, schema_version, json, updated_utc)
            VALUES($id, $server, $destination, $phase, $version, $json, $updated)
            ON CONFLICT(operation_id) DO UPDATE SET
                server_id=excluded.server_id, destination=excluded.destination, phase=excluded.phase,
                schema_version=excluded.schema_version, json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$server", entry.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$destination", entry.CanonicalDestination);
        command.Parameters.AddWithValue("$phase", entry.Phase.ToString());
        command.Parameters.AddWithValue("$version", entry.SchemaVersion);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one creation-journal row, including one this build cannot interpret.</summary>
    public async Task<CreationJournalRecord?> GetCreationJournalAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT operation_id, schema_version, json FROM creation_journal WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <summary>Every creation-journal row, oldest first, including unreadable ones.</summary>
    public async Task<IReadOnlyList<CreationJournalRecord>> GetCreationJournalsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT operation_id, schema_version, json FROM creation_journal ORDER BY updated_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CreationJournalRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadRecord(reader));
        return results;
    }

    /// <summary>Removes a finalised creation-journal row. Lasting evidence lives in instance history.</summary>
    public async Task DeleteCreationJournalAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM creation_journal WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns one row into a record, marking rather than discarding anything this build cannot read.
    /// </summary>
    /// <remarks>
    /// A newer schema version or malformed payload must never be treated as "no operation here":
    /// that would let a later run reuse a destination another version is mid-way through owning.
    /// </remarks>
    private static CreationJournalRecord ReadRecord(SqliteDataReader reader)
    {
        var operationId = Guid.TryParse(reader.GetString(0), out var parsed) ? parsed : Guid.Empty;
        var version = reader.GetInt32(1);
        if (version > CreationJournalEntry.CurrentSchemaVersion)
            return new CreationJournalRecord(operationId, version, null,
                $"Written by a newer version of ChunkPilot (journal schema {version}).");
        try
        {
            var entry = JsonSerializer.Deserialize<CreationJournalEntry>(reader.GetString(2), ProtocolJson.Options);
            return entry is null
                ? new CreationJournalRecord(operationId, version, null, "The journal entry was empty.")
                : new CreationJournalRecord(operationId, version, entry, "");
        }
        catch (JsonException exception)
        {
            return new CreationJournalRecord(operationId, version, null,
                $"The journal entry could not be read: {exception.Message}");
        }
    }

    public async Task RecordEulaAcceptanceAsync(
        Guid serverId,
        DateTimeOffset acceptedAt,
        string eulaUrl,
        string reference,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO eula_acceptance(server_id, accepted_utc, eula_url, reference)
            VALUES($server, $accepted, $url, $reference)
            ON CONFLICT(server_id) DO UPDATE SET
                accepted_utc=excluded.accepted_utc, eula_url=excluded.eula_url, reference=excluded.reference
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$accepted", acceptedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$url", eulaUrl);
        command.Parameters.AddWithValue("$reference", reference);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordInstanceHistoryAsync(
        Guid serverId,
        string action,
        string source,
        string sha256,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO instance_history(server_id, timestamp_utc, action, source, sha256, detail)
            VALUES($server, $timestamp, $action, $source, $sha256, $detail)
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagedInstallEvidence?> GetManagedInstallEvidenceAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_utc, source, sha256, detail
            FROM instance_history
            WHERE server_id=$server AND action='Installed'
            ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ManagedInstallEvidence(
            DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public async Task RecordHourlyStatisticsAsync(Guid serverId, StatisticsSample sample, CancellationToken cancellationToken = default)
    {
        var hour = new DateTimeOffset(sample.Timestamp.Year, sample.Timestamp.Month, sample.Timestamp.Day,
            sample.Timestamp.Hour, 0, 0, TimeSpan.Zero);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO statistics_hourly(server_id, hour_utc, cpu_average, ram_average, sample_count)
            VALUES($server, $hour, $cpu, $ram, 1)
            ON CONFLICT(server_id, hour_utc) DO UPDATE SET
                cpu_average=((cpu_average * sample_count) + excluded.cpu_average) / (sample_count + 1),
                ram_average=CAST(((ram_average * sample_count) + excluded.ram_average) / (sample_count + 1) AS INTEGER),
                sample_count=sample_count + 1
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$hour", hour.ToString("O"));
        command.Parameters.AddWithValue("$cpu", sample.CpuPercent);
        command.Parameters.AddWithValue("$ram", sample.WorkingSetBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertUpdateSourceAsync(UpdateSource source, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO update_sources(server_id, json, updated_utc) VALUES($server, $json, $updated)
            ON CONFLICT(server_id) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$server", source.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(source, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateSource?> GetUpdateSourceAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM update_sources WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<UpdateSource>(value, ProtocolJson.Options);
    }

    public async Task RecordUpdateCheckAsync(UpdateCheckResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO update_checks(server_id, checked_utc, json) VALUES($server, $checked, $json)
                """;
            insert.Parameters.AddWithValue("$server", result.ServerId.ToString("D"));
            insert.Parameters.AddWithValue("$checked", result.CheckedAt.UtcDateTime.ToString("O"));
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, ProtocolJson.Options));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (result.Source is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                INSERT INTO update_sources(server_id, json, updated_utc) VALUES($server, $json, $updated)
                ON CONFLICT(server_id) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
                """;
            var source = result.Source with { LastCheckedAt = result.CheckedAt };
            update.Parameters.AddWithValue("$server", source.ServerId.ToString("D"));
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(source, ProtocolJson.Options));
            update.Parameters.AddWithValue("$updated", result.CheckedAt.UtcDateTime.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateCheckResult?> GetLatestUpdateCheckAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM update_checks WHERE server_id=$server ORDER BY checked_utc DESC LIMIT 1";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<UpdateCheckResult>(value, ProtocolJson.Options);
    }

    public async Task UpsertVersionSnapshotAsync(VersionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO version_snapshots(id, server_id, json, installed_utc)
            VALUES($id, $server, $json, $installed)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$server", snapshot.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, ProtocolJson.Options));
        command.Parameters.AddWithValue("$installed", snapshot.InstalledAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VersionSnapshot>> GetVersionSnapshotsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM version_snapshots WHERE server_id=$server ORDER BY installed_utc DESC";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<VersionSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<VersionSnapshot>(reader.GetString(0), ProtocolJson.Options);
            if (snapshot is not null)
                results.Add(snapshot);
        }
        return results;
    }

    public async Task DeleteVersionSnapshotRecordAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM version_snapshots WHERE id=$id";
        command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertUpdatePreferencesAsync(
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO update_preferences(server_id, json) VALUES($server, $json)
            ON CONFLICT(server_id) DO UPDATE SET json=excluded.json
            """;
        command.Parameters.AddWithValue("$server", preferences.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preferences, ProtocolJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdatePreferences> GetUpdatePreferencesAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM update_preferences WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null
            ? new UpdatePreferences { ServerId = serverId }
            : JsonSerializer.Deserialize<UpdatePreferences>(value, ProtocolJson.Options)
              ?? new UpdatePreferences { ServerId = serverId };
    }

    public async Task RecordMigrationDecisionsAsync(
        IEnumerable<MigrationDecision> decisions,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var decision in decisions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO migration_decisions(operation_id, relative_path, json)
                VALUES($operation, $path, $json)
                ON CONFLICT(operation_id, relative_path) DO UPDATE SET json=excluded.json
                """;
            command.Parameters.AddWithValue("$operation", decision.UpdateOperationId.ToString("D"));
            command.Parameters.AddWithValue("$path", decision.RelativePath);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(decision, ProtocolJson.Options));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordUpdateDownloadAsync(
        Guid operationId,
        Guid serverId,
        UpdateProvider provider,
        PackVersionInfo version,
        long sizeBytes,
        string sha256,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO update_downloads(
                operation_id, server_id, provider, version_id, source_url, file_name,
                size_bytes, sha256, provider_hash, status, completed_utc)
            VALUES($operation, $server, $provider, $version, $url, $file, $size, $sha256, $providerHash, $status, $completed)
            ON CONFLICT(operation_id) DO UPDATE SET
                size_bytes=excluded.size_bytes, sha256=excluded.sha256,
                provider_hash=excluded.provider_hash, status=excluded.status,
                completed_utc=excluded.completed_utc
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$version", version.VersionId);
        command.Parameters.AddWithValue("$url", version.DownloadUrl);
        command.Parameters.AddWithValue("$file", version.FileName);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$providerHash",
            !string.IsNullOrWhiteSpace(version.Sha512) ? $"sha512:{version.Sha512}" :
            !string.IsNullOrWhiteSpace(version.Sha256) ? $"sha256:{version.Sha256}" :
            !string.IsNullOrWhiteSpace(version.Sha1) ? $"sha1:{version.Sha1}" : "");
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRollbackAsync(
        Guid serverId,
        string fromVersion,
        string toVersion,
        string result,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rollback_history(server_id, timestamp_utc, from_version, to_version, result, detail)
            VALUES($server, $timestamp, $fromVersion, $toVersion, $result, $detail)
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$fromVersion", fromVersion);
        command.Parameters.AddWithValue("$toVersion", toVersion);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UpdateHistoryEntry>> GetUpdateHistoryAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<UpdateHistoryEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var checks = connection.CreateCommand())
        {
            checks.CommandText = """
                SELECT checked_utc, json FROM update_checks
                WHERE server_id=$server ORDER BY checked_utc DESC LIMIT 100
                """;
            checks.Parameters.AddWithValue("$server", serverId.ToString("D"));
            await using var reader = await checks.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var check = JsonSerializer.Deserialize<UpdateCheckResult>(
                    reader.GetString(1), ProtocolJson.Options);
                if (check is not null)
                    results.Add(new UpdateHistoryEntry
                    {
                        Timestamp = DateTimeOffset.Parse(reader.GetString(0),
                            System.Globalization.CultureInfo.InvariantCulture),
                        Kind = "Check",
                        Summary = check.Status.ToString(),
                        Detail = check.Message,
                        Success = check.Status != ServerUpdateStatus.CheckUnavailable
                    });
            }
        }
        await using (var downloads = connection.CreateCommand())
        {
            downloads.CommandText = """
                SELECT completed_utc, version_id, file_name, status, sha256
                FROM update_downloads WHERE server_id=$server
                ORDER BY completed_utc DESC LIMIT 100
                """;
            downloads.Parameters.AddWithValue("$server", serverId.ToString("D"));
            await using var reader = await downloads.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(new UpdateHistoryEntry
                {
                    Timestamp = DateTimeOffset.Parse(reader.GetString(0),
                        System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "Download",
                    Summary = $"{reader.GetString(1)} · {reader.GetString(3)}",
                    Detail = $"{reader.GetString(2)} · SHA-256 {reader.GetString(4)}",
                    Success = !reader.GetString(3).StartsWith("Rejected", StringComparison.OrdinalIgnoreCase)
                });
        }
        await using (var rollbacks = connection.CreateCommand())
        {
            rollbacks.CommandText = """
                SELECT timestamp_utc, from_version, to_version, result, detail
                FROM rollback_history WHERE server_id=$server
                ORDER BY timestamp_utc DESC LIMIT 100
                """;
            rollbacks.Parameters.AddWithValue("$server", serverId.ToString("D"));
            await using var reader = await rollbacks.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(new UpdateHistoryEntry
                {
                    Timestamp = DateTimeOffset.Parse(reader.GetString(0),
                        System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "Rollback",
                    Summary = $"{reader.GetString(1)} → {reader.GetString(2)} · {reader.GetString(3)}",
                    Detail = reader.GetString(4),
                    Success = reader.GetString(3).Equals("Success", StringComparison.OrdinalIgnoreCase) ||
                              reader.GetString(3).Equals("Recovered", StringComparison.OrdinalIgnoreCase)
                });
        }
        return results.OrderByDescending(item => item.Timestamp).Take(200).ToArray();
    }

    public Task UpsertCapabilityProfileAsync(
        ServerCapabilityProfile profile,
        CancellationToken cancellationToken = default) =>
        UpsertServerJsonAsync("capability_profiles", profile.ServerId, profile, cancellationToken);

    public Task<ServerCapabilityProfile?> GetCapabilityProfileAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<ServerCapabilityProfile>("capability_profiles", serverId, cancellationToken);

    public async Task UpsertManagedJavaRuntimeAsync(
        ManagedJavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO managed_java_runtimes(id, json, installed_utc)
            VALUES($id, $json, $installed)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json, installed_utc=excluded.installed_utc
            """;
        command.Parameters.AddWithValue("$id", runtime.Id.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(runtime, ProtocolJson.Options));
        command.Parameters.AddWithValue("$installed", runtime.InstalledAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedJavaRuntimesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM managed_java_runtimes ORDER BY installed_utc DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ManagedJavaRuntime>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<ManagedJavaRuntime>(reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    public async Task DeleteManagedJavaRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM managed_java_runtimes
            WHERE id=$id AND NOT EXISTS(
                SELECT 1 FROM java_assignments WHERE runtime_id=$id)
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("The managed runtime is assigned to a server or does not exist.");
    }

    public async Task SetJavaAssignmentAsync(
        Guid serverId,
        Guid? runtimeId,
        string javaPath,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO java_assignments(server_id, runtime_id, java_path, source, updated_utc)
            VALUES($server, $runtime, $path, $source, $updated)
            ON CONFLICT(server_id) DO UPDATE SET
                runtime_id=excluded.runtime_id, java_path=excluded.java_path,
                source=excluded.source, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$runtime", (object?)runtimeId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", javaPath);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JavaRuntimeAssignment?> GetJavaAssignmentAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT runtime_id, java_path, source, updated_utc
            FROM java_assignments WHERE server_id=$server
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new JavaRuntimeAssignment
        {
            ServerId = serverId,
            RuntimeId = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
            JavaPath = reader.GetString(1),
            Source = reader.GetString(2),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    public Task UpsertNetworkConfigurationAsync(
        NetworkConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        UpsertServerJsonAsync("network_configurations", configuration.ServerId, configuration, cancellationToken);

    public Task<NetworkConfiguration?> GetNetworkConfigurationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<NetworkConfiguration>("network_configurations", serverId, cancellationToken);

    /// <summary>
    /// Stores one server's Direct internet intent and the evidence needed to prove a router mapping is
    /// ChunkPilot's own.
    /// </summary>
    /// <remarks>
    /// A table of its own rather than a field on <see cref="NetworkConfiguration"/>: the App replaces
    /// that record wholesale when the user saves a networking method, which would discard Agent-owned
    /// ownership evidence and leave a real mapping on the router with nothing left to prove who made it.
    /// A server with no row has never opted in, so every existing server starts with router mapping off.
    /// </remarks>
    public Task UpsertRouterMappingAsync(
        RouterMappingRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertServerJsonAsync("router_mappings", record.ServerId, record, cancellationToken);
    }

    /// <remarks>
    /// Every row is brought up to the current shape as it is read, so no caller anywhere has to know
    /// which build wrote it.
    /// </remarks>
    public async Task<RouterMappingRecord?> GetRouterMappingAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var stored = await GetServerJsonAsync<RouterMappingRecord>("router_mappings", serverId, cancellationToken)
            .ConfigureAwait(false);
        return stored is null ? null : RouterMappingPolicy.UpgradeStoredRecord(stored);
    }

    public async Task<IReadOnlyList<RouterMappingRecord>> GetRouterMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM router_mappings ORDER BY updated_utc";
        var results = new List<RouterMappingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<RouterMappingRecord>(reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
                results.Add(RouterMappingPolicy.UpgradeStoredRecord(item));
        }
        return results;
    }

    public async Task DeleteRouterMappingAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM router_mappings WHERE server_id=$id";
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores one server's Windows Firewall access: the user's deliberate configuration and the
    /// evidence needed to prove a rule in Windows Firewall is ChunkPilot's own.
    /// </summary>
    /// <remarks>
    /// A table of its own, for the same reason the router mapping has one: the App replaces
    /// <see cref="NetworkConfiguration"/> wholesale when a networking method is saved, and that would
    /// discard Agent-owned ownership evidence while a real rule was still standing in Windows. A server
    /// with no row has no ChunkPilot-owned rule, which is the default for every existing server.
    /// </remarks>
    public Task UpsertFirewallAccessAsync(
        FirewallAccessRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertServerJsonAsync("firewall_access", record.ServerId, record, cancellationToken);
    }

    public Task<FirewallAccessRecord?> GetFirewallAccessAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<FirewallAccessRecord>("firewall_access", serverId, cancellationToken);

    public async Task<IReadOnlyList<FirewallAccessRecord>> GetFirewallAccessRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM firewall_access ORDER BY updated_utc";
        var results = new List<FirewallAccessRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<FirewallAccessRecord>(reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    public async Task DeleteFirewallAccessAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM firewall_access WHERE server_id=$id";
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task UpsertCrossplayConfigurationAsync(
        CrossplayConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        UpsertServerJsonAsync(
            "crossplay_configurations", configuration.ServerId, configuration, cancellationToken);

    public Task<CrossplayConfiguration?> GetCrossplayConfigurationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<CrossplayConfiguration>(
            "crossplay_configurations", serverId, cancellationToken);

    public async Task<IReadOnlyList<CrossplayConfiguration>> GetCrossplayConfigurationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM crossplay_configurations ORDER BY updated_utc";
        var results = new List<CrossplayConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<CrossplayConfiguration>(
                reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    public async Task UpsertDatapackInventoryAsync(
        DatapackInventoryItem item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO datapack_inventory(server_id, item_id, json, updated_utc)
            VALUES($server, $item, $json, $updated)
            ON CONFLICT(server_id, item_id) DO UPDATE SET
                json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$server", item.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$item", item.ItemId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DatapackInventoryItem>> GetDatapackInventoryAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json FROM datapack_inventory
            WHERE server_id=$server ORDER BY updated_utc
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var results = new List<DatapackInventoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<DatapackInventoryItem>(
                reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    public Task UpsertResourcePackConfigurationAsync(
        ResourcePackConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        UpsertServerJsonAsync(
            "resource_pack_configurations",
            configuration.ServerId,
            configuration,
            cancellationToken);

    public Task<ResourcePackConfiguration?> GetResourcePackConfigurationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<ResourcePackConfiguration>(
            "resource_pack_configurations", serverId, cancellationToken);

    public async Task UpsertAutomationRecipeAsync(
        AutomationRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var errors = AutomationPolicy.Validate(recipe);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_recipes(id, server_id, json, updated_utc)
            VALUES($id, $server, $json, $updated)
            ON CONFLICT(id) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", recipe.Id.ToString("D"));
        command.Parameters.AddWithValue("$server", recipe.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(recipe, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutomationRecipe>> GetAutomationRecipesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM automation_recipes ORDER BY updated_utc";
        var results = new List<AutomationRecipe>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var recipe = JsonSerializer.Deserialize<AutomationRecipe>(reader.GetString(0), ProtocolJson.Options);
            if (recipe is not null)
                results.Add(recipe);
        }
        return results;
    }

    public async Task DeleteAutomationRecipeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM automation_recipes WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertProcessIdentityAsync(
        ProcessIdentity identity,
        CancellationToken cancellationToken = default) =>
        await UpsertServerJsonAsync("process_identities", identity.ServerId, identity, cancellationToken)
            .ConfigureAwait(false);

    public Task<ProcessIdentity?> GetProcessIdentityAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetServerJsonAsync<ProcessIdentity>("process_identities", serverId, cancellationToken);

    public async Task RemoveProcessIdentityAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM process_identities WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UiSessionRegistrationResult> RegisterUiSessionAsync(
        ApplicationSession session,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSession? previous = null;
        await using (var previousCommand = connection.CreateCommand())
        {
            previousCommand.CommandText = """
                SELECT json FROM application_sessions
                ORDER BY started_utc DESC LIMIT 1
                """;
            var value = await previousCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is string json)
                previous = JsonSerializer.Deserialize<ApplicationSession>(json, ProtocolJson.Options);
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO application_sessions(
                    session_id, json, started_utc, heartbeat_utc, closed_utc, exit_kind)
                VALUES($id, $json, $started, $heartbeat, NULL, NULL)
                """;
            command.Parameters.AddWithValue("$id", session.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(session, ProtocolJson.Options));
            command.Parameters.AddWithValue("$started", session.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$heartbeat", session.LastHeartbeatAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var unexpected = previous?.ExitKind == ApplicationExitKind.Unexpected ||
                         previous is { ClosedAt: null } &&
                         DateTimeOffset.UtcNow - previous.LastHeartbeatAt > TimeSpan.FromSeconds(5);
        return new UiSessionRegistrationResult(
            session,
            unexpected,
            unexpected
                ? "The previous ChunkPilot session did not record a clean handoff. Managed process and connectivity state were rechecked before this session opened."
                : "");
    }

    public async Task HeartbeatUiSessionAsync(
        Guid sessionId,
        IReadOnlyList<Guid> runningServerIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSession? session;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT json FROM application_sessions WHERE session_id=$id";
            read.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            var json = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            session = json is null ? null :
                JsonSerializer.Deserialize<ApplicationSession>(json, ProtocolJson.Options);
        }
        if (session is null)
            return;
        var updated = session with
        {
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            RunningServerIds = runningServerIds
        };
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE application_sessions
            SET json=$json, heartbeat_utc=$heartbeat
            WHERE session_id=$id AND closed_utc IS NULL
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(updated, ProtocolJson.Options));
        command.Parameters.AddWithValue("$heartbeat", updated.LastHeartbeatAt.ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseUiSessionAsync(
        Guid sessionId,
        ApplicationExitKind exitKind,
        IReadOnlyList<Guid> runningServerIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSession? session;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT json FROM application_sessions WHERE session_id=$id";
            read.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            var json = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            session = json is null ? null :
                JsonSerializer.Deserialize<ApplicationSession>(json, ProtocolJson.Options);
        }
        if (session is null)
            return;
        var closed = session with
        {
            ClosedAt = DateTimeOffset.UtcNow,
            ExitKind = exitKind,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            RunningServerIds = runningServerIds
        };
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE application_sessions SET
                json=$json, heartbeat_utc=$heartbeat, closed_utc=$closed, exit_kind=$kind
            WHERE session_id=$id
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(closed, ProtocolJson.Options));
        command.Parameters.AddWithValue("$heartbeat", closed.LastHeartbeatAt.ToString("O"));
        command.Parameters.AddWithValue("$closed", closed.ClosedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$kind", exitKind.ToString());
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationSession?> GetLatestOpenUiSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json FROM application_sessions
            WHERE closed_utc IS NULL
            ORDER BY started_utc DESC LIMIT 1
            """;
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? null
            : JsonSerializer.Deserialize<ApplicationSession>(json, ProtocolJson.Options);
    }

    public async Task SetRunningStateAsync(
        Guid serverId,
        AutostartMode autostartMode,
        bool wasRunning,
        LifecycleIntentKind lastIntent,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_running_state(
                server_id, autostart_mode, was_running, last_intent, updated_utc)
            VALUES($server, $mode, $running, $intent, $updated)
            ON CONFLICT(server_id) DO UPDATE SET
                autostart_mode=excluded.autostart_mode, was_running=excluded.was_running,
                last_intent=excluded.last_intent, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$mode", autostartMode.ToString());
        command.Parameters.AddWithValue("$running", wasRunning ? 1 : 0);
        command.Parameters.AddWithValue("$intent", lastIntent.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServerRunningState>> GetRunningStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, autostart_mode, was_running, last_intent, updated_utc
            FROM server_running_state
            """;
        var results = new List<ServerRunningState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var serverId))
                continue;
            _ = Enum.TryParse<AutostartMode>(reader.GetString(1), out var mode);
            _ = Enum.TryParse<LifecycleIntentKind>(reader.GetString(3), out var intent);
            _ = DateTimeOffset.TryParse(reader.GetString(4), out var updated);
            results.Add(new ServerRunningState(serverId, mode, reader.GetInt32(2) != 0, intent, updated));
        }
        return results;
    }

    private async Task UpsertServerJsonAsync<T>(
        string table,
        Guid serverId,
        T value,
        CancellationToken cancellationToken)
    {
        if (table is not ("capability_profiles" or "network_configurations" or
            "crossplay_configurations" or "resource_pack_configurations" or
            "process_identities" or "router_mappings" or "firewall_access"))
            throw new ArgumentOutOfRangeException(nameof(table));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {table}(server_id, json, updated_utc)
            VALUES($server, $json, $updated)
            ON CONFLICT(server_id) DO UPDATE SET json=excluded.json, updated_utc=excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetServerJsonAsync<T>(
        string table,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        if (table is not ("capability_profiles" or "network_configurations" or
            "crossplay_configurations" or "resource_pack_configurations" or
            "process_identities" or "router_mappings" or "firewall_access"))
            throw new ArgumentOutOfRangeException(nameof(table));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT json FROM {table} WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json
            ? JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)
            : default;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
