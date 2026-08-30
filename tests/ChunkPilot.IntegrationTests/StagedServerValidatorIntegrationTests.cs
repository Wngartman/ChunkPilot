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

    [Fact]
    public async Task Exact_owned_non_loopback_listener_is_rejected_and_cleaned()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateRoot();
        var identityPath = Path.Combine(root, "staged-non-loopback-listener.pid");
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-non-loopback-listener.jar"), [0x01]);
        try
        {
            var result = await new StagedServerValidator().ValidateAsync(
                FakeJavaPath(),
                root,
                "staged-non-loopback-listener.jar",
                usesArgumentFile: false,
                minimumRamMb: 512,
                maximumRamMb: 512,
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(result.Succeeded);
            Assert.True(result.ReadinessConfirmed);
            Assert.Contains("unexpected non-loopback network endpoint", result.Summary,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(identityPath), "The listener fixture never reached its owned listener state.");
            Assert.False(await IsExactProcessAliveAsync(ReadIdentity(identityPath), TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(Path.Combine(root, "server.properties")),
                "Validation did not remove its temporary server.properties file.");
        }
        finally
        {
            await KillExactFixtureProcessIfPresentAsync(identityPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exact_owned_wildcard_udp_endpoint_is_rejected_and_cleaned()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateRoot();
        var identityPath = Path.Combine(root, "staged-non-loopback-udp.pid");
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-non-loopback-udp.jar"), [0x01]);
        try
        {
            var result = await new StagedServerValidator().ValidateAsync(
                FakeJavaPath(),
                root,
                "staged-non-loopback-udp.jar",
                usesArgumentFile: false,
                minimumRamMb: 512,
                maximumRamMb: 512,
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(result.Succeeded);
            Assert.True(result.ReadinessConfirmed);
            Assert.Contains("unexpected non-loopback network endpoint", result.Summary,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(identityPath), "The UDP fixture never reached its owned endpoint state.");
            Assert.False(await IsExactProcessAliveAsync(ReadIdentity(identityPath), TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(Path.Combine(root, "server.properties")),
                "Validation did not remove its temporary server.properties file.");
        }
        finally
        {
            await KillExactFixtureProcessIfPresentAsync(identityPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exact_owned_outbound_non_loopback_tcp_connection_is_rejected_and_cleaned()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var address = FindNonLoopbackIpv4Address();
        if (address is null)
            return;

        var root = CreateRoot();
        var identityPath = Path.Combine(root, "staged-outbound-tcp.pid");
        var listener = new TcpListener(address, 0);
        listener.Start();
        await File.WriteAllTextAsync(
            Path.Combine(root, "staged-outbound-tcp-target.txt"),
            FormattableString.Invariant($"{address}|{((IPEndPoint)listener.LocalEndpoint).Port}"));
        await File.WriteAllBytesAsync(Path.Combine(root, "staged-outbound-tcp.jar"), [0x01]);
        try
        {
            var validation = new StagedServerValidator().ValidateAsync(
                FakeJavaPath(),
                root,
                "staged-outbound-tcp.jar",
                usesArgumentFile: false,
                minimumRamMb: 512,
                maximumRamMb: 512,
                timeout: TimeSpan.FromSeconds(15));
            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await validation;

            Assert.False(result.Succeeded);
            Assert.True(result.ReadinessConfirmed);
            Assert.Contains("unexpected non-loopback network endpoint", result.Summary,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(identityPath), "The outbound fixture never established its TCP connection.");
            Assert.False(await IsExactProcessAliveAsync(ReadIdentity(identityPath), TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(Path.Combine(root, "server.properties")),
                "Validation did not remove its temporary server.properties file.");
        }
        finally
        {
            listener.Stop();
            await KillExactFixtureProcessIfPresentAsync(identityPath);
            Directory.Delete(root, recursive: true);
        }
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
