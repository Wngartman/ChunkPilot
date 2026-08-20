using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class PublicConnectivityArchitecturePolicyTests
{
    [Fact]
    public void Firewall_remains_durable_configuration_with_no_window_guardian_or_lease_backend()
    {
        var root = RepositoryRoot();
        var productionFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .ToArray();
        var source = string.Join('\n', productionFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("UiSessionNetworkOwnership", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaseSessionId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FirewallLeaseGuardian", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GuardFirewall", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeferredWindowsFirewall", source, StringComparison.Ordinal);

        var operations = Enum.GetNames<FirewallHelperOperation>();
        Assert.Equal(["Create", "Update", "Remove"], operations);

        var helperSource = string.Join('\n', productionFiles
            .Where(path => path.Contains("ChunkPilot.FirewallHelper", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Assert.DoesNotContain("PublicConnectivityLease", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessCreationIdentity", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_exit_and_startup_boundaries_use_epoch_fencing_and_tracked_restoration()
    {
        var root = RepositoryRoot();
        var pipe = File.ReadAllText(Path.Combine(root, "src", "ChunkPilot.Agent", "AgentPipeServer.cs"));
        var supervisor = File.ReadAllText(Path.Combine(root, "src", "ChunkPilot.Agent", "ServerSupervisor.cs"));
        var router = File.ReadAllText(Path.Combine(root, "src", "ChunkPilot.Agent", "RouterMappingCoordinator.cs"));
        var publicCoordinator = File.ReadAllText(
            Path.Combine(root, "src", "ChunkPilot.Agent", "PublicConnectivityCoordinator.cs"));

        Assert.Contains("BeginApplicationExit", pipe, StringComparison.Ordinal);
        Assert.Contains("StopAllForApplicationExitAsync", pipe, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAllAsync(\n                    source, escalateOnFailure: true, cancellationToken: CancellationToken.None)",
            pipe, StringComparison.Ordinal);
        Assert.Contains("RestoreStartupStateAsync", supervisor, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(async () =>", supervisor, StringComparison.Ordinal);
        Assert.Contains("authority.Demand(record, \"serialized router gate execution\")", router,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAllAsync", router, StringComparison.Ordinal);
        Assert.Contains("CleanupRevokedAsync", publicCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupAllAsync", publicCoordinator, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
