using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class StagedServerValidatorIntegrationTests
{
    [Fact]
    public async Task Root_that_spawns_a_child_then_exits_leaves_no_validation_descendant()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateRoot();
        var identityPath = Path.Combine(root, "staged-validation-child.pid");
        const string originalProperties = "motd=preserve-this\r\nwhite-list=true\r\n";
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), originalProperties);
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-spawn-child-and-exit.jar"), [0x01]);
        try
        {
            var result = await new StagedServerValidator().ValidateAsync(
                FakeJavaPath(),
                root,
                "staged-spawn-child-and-exit.jar",
                usesArgumentFile: false,
                minimumRamMb: 512,
                maximumRamMb: 512,
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(result.Succeeded);
            Assert.Contains("exited before readiness with code 37", result.Summary,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(identityPath), "The root fixture did not prove that it spawned its child.");
            var child = ReadIdentity(identityPath);
            Assert.False(await IsExactProcessAliveAsync(child, TimeSpan.FromSeconds(5)),
                $"Staged validation descendant {child.ProcessId} survived cleanup.");
            Assert.Equal(originalProperties,
                await File.ReadAllTextAsync(Path.Combine(root, "server.properties")));
        }
        finally
        {
            await KillExactFixtureProcessIfPresentAsync(identityPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("tcp", false)]
    [InlineData("udp", false)]
    [InlineData("connection", true)]
    [InlineData("stale", false)]
    [InlineData("collector", false)]
    public async Task Synthetic_endpoint_decision_uses_real_loopback_process_and_exact_cleanup(string kind, bool expectedSuccess)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateRoot();
        const string original = "level-name=intended-world\r\nserver-ip=\r\nserver-port=25565\r\nmotd=preserve\r\n";
        await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), original);
        Directory.CreateDirectory(Path.Combine(root, "intended-world"));
        await File.WriteAllTextAsync(Path.Combine(root, "intended-world", "level.dat"), "irreplaceable sentinel");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        const string neoForgeOriginal = "permissionHandler = \"custom:keep\"\nadvertiseDedicatedServerToLan = true\r\n";
        await File.WriteAllTextAsync(Path.Combine(root, "config", "neoforge-server.toml"), neoForgeOriginal);
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-loopback.jar"), [1]);
        var validator = new StagedServerValidator((job, attempt) =>
        {
            var endpoints = job.CaptureStableOwnedNetworkEndpoints().Select(e => e.ToObservation(attempt)).ToList();
            if (endpoints.FirstOrDefault(e => e.State == System.Net.NetworkInformation.TcpState.Listen) is not { } listener)
                return endpoints;
            var world = ServerPropertiesDocument.Parse(File.ReadAllText(Path.Combine(root, "server.properties"))).Get("level-name")!;
            Assert.Contains("advertiseDedicatedServerToLan = false", File.ReadAllText(
                Path.Combine(root, world, "serverconfig", "neoforge-server.toml")));
            if (kind == "collector") throw new IOException("Synthetic table read failure");
            endpoints.Add(listener with
            {
                LocalAddress = "192.0.2.10", LocalPort = 49000,
                Transport = kind == "udp" ? ChunkPilot.Core.StartupEndpointTransport.Udp : ChunkPilot.Core.StartupEndpointTransport.Tcp,
                State = kind == "udp" ? null : kind == "connection" ? System.Net.NetworkInformation.TcpState.Established :
                    System.Net.NetworkInformation.TcpState.Listen,
                AttemptId = kind == "stale" ? Guid.NewGuid() : attempt
            });
            return endpoints;
        });
        try
        {
            var result = await validator.ValidateAsync(FakeJavaPath(), root, "staged-loopback.jar", false,
                512, 512, TimeSpan.FromSeconds(15), ecosystem: ChunkPilot.Core.ServerEcosystem.NeoForge);
            Assert.Equal(expectedSuccess, result.Succeeded);
            Assert.True(result.JobEmptyConfirmed);
            Assert.True(result.SelectedPortListenerAbsent);
            Assert.True(result.ConfigurationRestored);
            Assert.True(result.ValidationWorldRemoved);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(root, "server.properties")));
            Assert.Equal(neoForgeOriginal, await File.ReadAllTextAsync(Path.Combine(root, "config", "neoforge-server.toml")));
            Assert.Equal("irreplaceable sentinel", await File.ReadAllTextAsync(Path.Combine(root, "intended-world", "level.dat")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".chunkpilot-staging-*"));
            if (kind == "tcp") Assert.True(result.Network!.UnexpectedInboundListener);
            if (kind == "udp") { Assert.True(result.Network!.Unresolved); Assert.False(result.Network.UnexpectedInboundListener); }
            if (expectedSuccess) Assert.True(result.CleanStopConfirmed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_cancellation_stop_the_owned_process_and_remove_only_disposable_world(bool cancel)
    {
        var root = CreateRoot();
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-loopback-no-readiness.jar"), [1]);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var run = new StagedServerValidator().ValidateAsync(FakeJavaPath(), root, "staged-loopback-no-readiness.jar",
                false, 512, 512, TimeSpan.FromSeconds(2), cancellationToken: cancellation.Token);
            if (cancel)
            {
                var failure = await Assert.ThrowsAsync<StagedServerCancelledException>(() => run);
                Assert.False(failure.Validation.Succeeded);
                Assert.True(failure.Validation.JobEmptyConfirmed);
                Assert.True(failure.Validation.SelectedPortListenerAbsent);
                Assert.True(failure.Validation.ValidationWorldRemoved);
            }
            else
            {
                var result = await run;
                Assert.False(result.Succeeded);
                Assert.True(result.JobEmptyConfirmed);
                Assert.True(result.SelectedPortListenerAbsent);
                Assert.True(result.CleanStopConfirmed);
            }
            Assert.False(File.Exists(Path.Combine(root, "server.properties")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".chunkpilot-staging-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-staged-validator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IPAddress? FindNonLoopbackIpv4Address() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.Equals(IPAddress.Any));

    private static string FakeJavaPath() =>
        Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0",
            "ChunkPilot.FakeServer.exe");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static ProcessIdentity ReadIdentity(string path)
    {
        var values = File.ReadAllText(path).Split('|', StringSplitOptions.TrimEntries);
        return new ProcessIdentity(
            int.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<bool> IsExactProcessAliveAsync(ProcessIdentity identity, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (!IsExactProcessAlive(identity))
                return false;
            await Task.Delay(50);
        }
        while (watch.Elapsed < timeout);
        return IsExactProcessAlive(identity);
    }

    private static bool IsExactProcessAlive(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task KillExactFixtureProcessIfPresentAsync(string identityPath)
    {
        if (!File.Exists(identityPath))
            return;
        var identity = ReadIdentity(identityPath);
        if (!IsExactProcessAlive(identity))
            return;
        using var process = Process.GetProcessById(identity.ProcessId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks);
}
