using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class StoppedPlayerAccessServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-offline-access-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid PlayerId = Guid.Parse("12522c38-b7e0-4b2d-8275-8c99b54cfc5e");
    private SafeFileService Files => new(new AppDataPaths(Path.Combine(root, "appdata")));
    private ServerDefinition Server => new()
    {
        RootPath = root, WorkingDirectory = root, Ecosystem = ServerEcosystem.NeoForge, MinecraftVersion = "1.21.1"
    };

    public StoppedPlayerAccessServiceTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(PlayerModerationAction.AddToWhitelist, "whitelist.json")]
    [InlineData(PlayerModerationAction.GrantOperator, "ops.json")]
    [InlineData(PlayerModerationAction.Ban, "banned-players.json")]
    public async Task Adds_only_exact_cached_player_preserving_other_files(PlayerModerationAction action, string target)
    {
        var cache = await SeedAsync();
        var result = await ApplyAsync(action);
        Assert.True(result.Success, result.Message);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, target)));
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(PlayerId, entry.GetProperty("uuid").GetGuid());
        Assert.Equal(cache, await File.ReadAllTextAsync(Path.Combine(root, "usercache.json")));
        if (action == PlayerModerationAction.GrantOperator)
            Assert.Equal(2, entry.GetProperty("level").GetInt32());
    }

    [Theory]
    [InlineData(PlayerModerationAction.RemoveFromWhitelist, "whitelist.json")]
    [InlineData(PlayerModerationAction.RemoveOperator, "ops.json")]
    [InlineData(PlayerModerationAction.Pardon, "banned-players.json")]
    public async Task Removes_only_requested_uuid_preserving_unknown_fields(PlayerModerationAction action, string target)
    {
        await SeedAsync();
        var otherId = Guid.NewGuid();
        var original = JsonSerializer.Serialize(new[]
        {
            new { uuid = PlayerId, name = "FixturePlayer", extra = "target" },
            new { uuid = otherId, name = "OtherPlayer", extra = "keep this custom field" }
        });
        await File.WriteAllTextAsync(Path.Combine(root, target), original);
        var result = await ApplyAsync(action);
        Assert.True(result.Success, result.Message);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, target)));
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(otherId, entry.GetProperty("uuid").GetGuid());
        Assert.Equal("keep this custom field", entry.GetProperty("extra").GetString());
        Assert.NotNull(result.Path);
        Assert.Equal(original, await File.ReadAllTextAsync(result.Path));
    }

    [Fact]
    public async Task Unknown_name_and_expired_cache_never_fabricate_uuid()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "usercache.json"), JsonSerializer.Serialize(new[]
        {
            new { uuid = PlayerId, name = "FixturePlayer", expiresOn = "2001-01-01 00:00:00 +0000" }
        }));
        var result = await ApplyAsync(PlayerModerationAction.AddToWhitelist);
        Assert.False(result.Success);
        Assert.Contains("No UUID was invented", result.Message);
        Assert.False(File.Exists(Path.Combine(root, "whitelist.json")));
    }

    [Fact]
    public async Task Conflicting_identity_and_malformed_file_are_preserved()
    {
        await SeedAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "ops.json"),
            JsonSerializer.Serialize(new[] { new { uuid = Guid.NewGuid(), name = "FixturePlayer" } }));
        Assert.False((await ApplyAsync(PlayerModerationAction.AddToWhitelist)).Success);
        const string malformed = "[{broken";
        await File.WriteAllTextAsync(Path.Combine(root, "ops.json"), malformed);
        await Assert.ThrowsAnyAsync<JsonException>(() => ApplyAsync(PlayerModerationAction.AddToWhitelist));
        Assert.Equal(malformed, await File.ReadAllTextAsync(Path.Combine(root, "ops.json")));
        Assert.False(File.Exists(Path.Combine(root, "whitelist.json")));
    }

    [Fact]
    public async Task External_file_creation_between_plan_and_write_is_not_overwritten()
    {
        await SeedAsync();
        var checks = 0;
        var service = new StoppedPlayerAccessService(Files);
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(Server,
            PlayerModerationAction.AddToWhitelist, "FixturePlayer", "", async token =>
            {
                if (++checks == 2)
                    await File.WriteAllTextAsync(Path.Combine(root, "whitelist.json"), "[\"external\"]", token);
            }));
        Assert.Equal("[\"external\"]", await File.ReadAllTextAsync(Path.Combine(root, "whitelist.json")));
    }

    [Theory]
    [InlineData(ServerEcosystem.Unknown, "1.21.1")]
    [InlineData(ServerEcosystem.Custom, "1.21.1")]
    [InlineData(ServerEcosystem.Vanilla, "1.6.4")]
    [InlineData(ServerEcosystem.Vanilla, "Unknown")]
    public void Unknown_or_old_layout_is_not_guessed(ServerEcosystem ecosystem, string version) =>
        Assert.False(StoppedPlayerAccessService.Supports(Server with { Ecosystem = ecosystem, MinecraftVersion = version }));

    [Fact]
    public void Invalid_or_empty_configuration_paths_do_not_break_capability_snapshots()
    {
        Assert.False(StoppedPlayerAccessService.Supports(Server with { RootPath = "" }));
        Assert.False(StoppedPlayerAccessService.Supports(Server with { WorkingDirectory = "\0" }));
    }

    [Fact]
    public async Task Kick_stays_live_only_and_noop_does_not_rewrite()
    {
        await SeedAsync();
        Assert.False((await ApplyAsync(PlayerModerationAction.Kick)).Success);
        Assert.True((await ApplyAsync(PlayerModerationAction.AddToWhitelist)).Success);
        var before = await File.ReadAllBytesAsync(Path.Combine(root, "whitelist.json"));
        Assert.True((await ApplyAsync(PlayerModerationAction.AddToWhitelist)).Success);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "whitelist.json")));
    }

    private async Task<string> SeedAsync()
    {
        var cache = JsonSerializer.Serialize(new[]
        {
            new { uuid = PlayerId, name = "FixturePlayer", expiresOn = "2099-01-01 00:00:00 +0000" }
        });
        await File.WriteAllTextAsync(Path.Combine(root, "usercache.json"), cache);
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), "op-permission-level=2\r\n");
        return cache;
    }

    private Task<OperationResult> ApplyAsync(PlayerModerationAction action) =>
        new StoppedPlayerAccessService(Files).ApplyAsync(Server, action, "FixturePlayer", "", _ => Task.CompletedTask);

    public void Dispose() => Directory.Delete(root, true);
}
