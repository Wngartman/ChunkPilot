using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ServerBindingEditorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-binding-edit-" + Guid.NewGuid().ToString("N"));
    private readonly SafeFileService files;
    public ServerBindingEditorTests()
    {
        Directory.CreateDirectory(root);
        files = new SafeFileService(new AppDataPaths(Path.Combine(root, "data")));
    }
    private ServerDefinition Server => new()
    {
        RootPath = root, WorkingDirectory = root, IsManaged = true,
        Ecosystem = ServerEcosystem.NeoForge, MinecraftVersion = "1.21.1", Port = 25586
    };

    [Theory]
    [InlineData(NetworkMode.HomeNetwork, "")]
    [InlineData(NetworkMode.PortForwarding, "")]
    [InlineData(NetworkMode.ThisComputerOnly, "127.0.0.1")]
    public async Task Binding_edit_preserves_world_security_port_comments_and_bom(NetworkMode mode, string expected)
    {
        const string original = "# owner comment\r\nserver-ip=127.0.0.1\r\nserver-port=25586\r\nlevel-name=precious-world\r\nonline-mode=true\r\nenable-rcon=false\r\nenable-query=false\r\nwhite-list=true\r\n";
        var path = Path.Combine(root, "server.properties");
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(path);
        var mutation = await ServerBindingEditor.ApplyAsync(Server, mode, files, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var result = await File.ReadAllTextAsync(path);
        Assert.Equal(original.Replace("server-ip=127.0.0.1", "server-ip=" + expected, StringComparison.Ordinal), result);
        await ServerBindingEditor.RollbackAsync(root, mutation, files, CancellationToken.None);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Rollback_refuses_to_overwrite_external_changes()
    {
        var path = Path.Combine(root, "server.properties");
        await File.WriteAllTextAsync(path, "server-ip=127.0.0.1\nserver-port=25586\n");
        var mutation = await ServerBindingEditor.ApplyAsync(Server, NetworkMode.HomeNetwork, files, CancellationToken.None);
        const string external = "# external owner edit\nserver-ip=127.0.0.1\nserver-port=25586\n";
        await File.WriteAllTextAsync(path, external);
        await Assert.ThrowsAsync<IOException>(() => ServerBindingEditor.RollbackAsync(root, mutation, files, CancellationToken.None));
        Assert.Equal(external, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false, ServerEcosystem.NeoForge)]
    [InlineData(true, ServerEcosystem.Custom)]
    [InlineData(true, ServerEcosystem.Unknown)]
    public async Task Unsupported_ownership_or_profiles_remain_unchanged(bool managed, ServerEcosystem ecosystem)
    {
        var path = Path.Combine(root, "server.properties");
        const string original = "server-ip=127.0.0.1\nserver-port=25586\n";
        await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerBindingEditor.ApplyAsync(
            Server with { IsManaged = managed, Ecosystem = ecosystem }, NetworkMode.HomeNetwork, files, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("server-ip=unresolved.invalid\nserver-port=25586\n")]
    [InlineData("server-ip=127.0.0.1\nserver-port=25565\n")]
    public async Task Unknown_binding_or_port_mismatch_is_not_repaired_by_guessing(string original)
    {
        var path = Path.Combine(root, "server.properties");
        await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerBindingEditor.ApplyAsync(Server, NetworkMode.HomeNetwork, files, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Local_only_intent_on_broad_saved_binding_is_explicitly_unapplied()
    {
        var summary = ServerConnectionSummaryPolicy.Build(new ServerSnapshot
        {
            Definition = Server, State = ServerState.Stopped,
            ConnectionEvidence = new() { Saved = new() { Known = true, Port = 25586, BindAddress = "" } }
        }, NetworkMode.ThisComputerOnly, "192.168.1.5", DateTimeOffset.UtcNow);
        Assert.True(summary.RequestedAudienceNotApplied);
        Assert.True(summary.CanApplyBinding);
        Assert.Contains("still allows other interfaces", summary.Explanation, StringComparison.Ordinal);
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
