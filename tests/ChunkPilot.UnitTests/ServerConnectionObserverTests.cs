using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ServerConnectionObserverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-binding-" + Guid.NewGuid().ToString("N"));
    public ServerConnectionObserverTests() => Directory.CreateDirectory(root);
    private ServerDefinition Server => new() { RootPath = root, WorkingDirectory = root, Ecosystem = ServerEcosystem.NeoForge, MinecraftVersion = "1.21.1" };
    private Task<ServerBindingEvidence> Read(ServerDefinition? server = null, CancellationToken cancellationToken = default) =>
        ServerConnectionObserver.ReadSavedAsync(server ?? Server, new SafeFileService(new AppDataPaths(Path.Combine(root, "data"))), cancellationToken);

    [Theory]
    [InlineData("server-ip=127.0.0.1\r\nserver-port=25586\r\n", true, "127.0.0.1", 25586)]
    [InlineData("# synthetic wildcard; no process launches\nserver-ip=\nserver-port=25587\n", true, "", 25587)]
    [InlineData("server-ip=unresolved.invalid\nserver-port=25586\n", false, "", 0)]
    [InlineData("server-ip=127.0.0.1\nserver-port=invalid\n", false, "", 0)]
    [InlineData("server-ip=127.0.0.1\nserver-port=70000\n", false, "", 0)]
    public async Task Reads_only_saved_configuration_without_mutation(string content, bool known, string bind, int port)
    {
        var path = Path.Combine(root, "server.properties");
        await File.WriteAllTextAsync(path, content);
        var value = await Read();
        Assert.Equal(known, value.Known);
        Assert.Equal(bind, value.BindAddress);
        Assert.Equal(port, value.Port);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Missing_or_oversized_configuration_is_unknown_not_a_lan_default()
    {
        Assert.False((await Read()).Known);
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), new string('x', 65_537));
        Assert.False((await Read()).Known);
    }

    [Fact]
    public async Task Unsupported_launch_and_working_directory_do_not_infer_properties()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), "server-ip=127.0.0.1\nserver-port=25586");
        Assert.False((await Read(Server with { Ecosystem = ServerEcosystem.Custom })).Known);
        Assert.False((await Read(Server with { WorkingDirectory = Path.Combine(root, "different") })).Known);
        Assert.False((await Read(Server with { RootPath = "" })).Known);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_publishing_a_successful_read()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), "server-ip=127.0.0.1\nserver-port=25586");
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(cancellationToken: source.Token));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
