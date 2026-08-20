using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class WindowsFirewallStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-firewall-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Schema_v6_adds_an_empty_firewall_table_without_claiming_old_servers()
    {
        var paths = new AppDataPaths(root);
        paths.EnsureCreated();
        var server = new ServerDefinition { Id = Guid.NewGuid(), Name = "Preserved", RootPath = root };
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE servers (id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO servers(id,json,updated_utc) VALUES($id,$json,$updated);
                PRAGMA user_version=5;
                """;
            command.Parameters.AddWithValue("$id", server.Id.ToString("D"));
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(server, ProtocolJson.Options));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();

        Assert.Equal("Preserved", Assert.Single(await store.GetServersAsync()).Name);
        Assert.Null(await store.GetFirewallAccessAsync(server.Id));
        Assert.Empty(await store.GetFirewallAccessRecordsAsync());
        await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Ownership_stale_failure_and_cleanup_evidence_round_trip()
    {
        var paths = new AppDataPaths(root);
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var record = new FirewallAccessRecord
        {
            ServerId = Guid.NewGuid(),
            Configured = true,
            RuleId = Guid.NewGuid(),
            RuleName = "stable rule",
            ProgramPath = @"D:\Java\bin\java.exe",
            Port = 25566,
            Profiles = FirewallProfile.Private,
            PublicApproved = false,
            ConfiguredAt = DateTimeOffset.UtcNow.AddDays(-1),
            RemovalPending = true,
            ServerRemoved = true,
            LastFailure = FirewallAccessFailure.RemovalFailed,
            LastOperationDetail = "kept for recovery",
            LastCheckedAt = DateTimeOffset.UtcNow
        };

        await store.UpsertFirewallAccessAsync(record);
        await store.DisposeAsync();
        await using var reopened = new ChunkPilotStore(paths);
        await reopened.InitializeAsync();
        var actual = await reopened.GetFirewallAccessAsync(record.ServerId);

        Assert.Equal(record, actual);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
