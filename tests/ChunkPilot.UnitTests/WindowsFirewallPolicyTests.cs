using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class WindowsFirewallPolicyTests
{
    private static readonly Guid ServerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RuleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Java = @"D:\ChunkPilot\managed-java\bin\java.exe";

    [Fact]
    public void Exact_plan_is_narrow_and_verifies_every_relevant_property()
    {
        var plan = Plan();
        var rule = Exact(plan);

        Assert.Empty(WindowsFirewallPolicy.Differences(plan, rule));
        Assert.Equal(FirewallRuleDirection.Inbound, plan.Direction);
        Assert.Equal(FirewallRuleAction.Allow, plan.Action);
        Assert.Equal(WindowsFirewallPolicy.ProtocolTcp, plan.Protocol);
        Assert.Equal(FirewallProfile.Private, plan.Profiles);
        Assert.False(plan.EdgeTraversal);
        Assert.Equal("*", plan.RemoteAddresses);
        Assert.Contains(RuleId.ToString("D"), plan.RuleName, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> UnsafeDifferences()
    {
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Enabled = false })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Direction = FirewallRuleDirection.Outbound })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Action = FirewallRuleAction.Block })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Protocol = WindowsFirewallPolicy.ProtocolUdp })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalPorts = "25560-25570" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemotePorts = "443" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalAddresses = "192.168.1.4" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteAddresses = "LocalSubnet" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ApplicationName = @"D:\Other\java.exe" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Profiles = FirewallProfile.Public })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { AppliesToAllProfiles = true })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { InterfaceTypes = "Wireless" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { EdgeTraversal = true })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { EdgeTraversalOptions = 1 })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ServiceName = "AnyService" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Interfaces = ["Ethernet"] })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalAppPackageId = "S-1-15-2-fixture" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalUserOwner = "S-1-5-21-fixture" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalUserAuthorizedList = "fixture" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteUserAuthorizedList = "fixture" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteMachineAuthorizedList = "fixture" })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { SecureFlags = 1 })];
        yield return [new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { UnavailableFields = FirewallRuleUnavailableFields.LocalAppPackageId })];
    }

    [Theory]
    [MemberData(nameof(UnsafeDifferences))]
    public void Postcondition_rejects_broad_or_changed_shape(
        Func<FirewallRuleSnapshot, FirewallRuleSnapshot> change)
    {
        var plan = Plan();
        Assert.NotEmpty(WindowsFirewallPolicy.Differences(plan, change(Exact(plan))));
    }

    [Fact]
    public void Ownership_requires_persisted_identity_group_and_description()
    {
        var plan = Plan();
        var record = Record(plan);
        var exact = Exact(plan);

        Assert.True(WindowsFirewallPolicy.ProvesOwnership(record, exact));
        Assert.False(WindowsFirewallPolicy.ProvesOwnership(record, exact with { Grouping = "Someone else" }));
        Assert.False(WindowsFirewallPolicy.ProvesOwnership(record, exact with { Description = "lookalike" }));
        Assert.False(WindowsFirewallPolicy.ProvesOwnership(record with { Configured = false }, exact));
    }

    [Fact]
    public void Foreign_broad_allow_never_satisfies_exact_requirement_and_block_is_not_ignored()
    {
        var plan = Plan();
        var broad = Exact(plan) with
        {
            Name = "Administrator broad Java allow",
            Grouping = "Administrator",
            Description = "foreign",
            LocalPorts = "*"
        };
        var block = broad with { Name = "Administrator block", Action = FirewallRuleAction.Block };

        Assert.Null(WindowsFirewallPolicy.FindCoveringAllowRule(plan, [broad], Guid.Empty));
        Assert.Equal(FirewallRuleCoverage.BroadUnrestricted,
            WindowsFirewallPolicy.EvaluateRuleCoverage(plan, broad).Coverage);
        Assert.Same(block, WindowsFirewallPolicy.FindApplicableBlockRule(plan, [block]));
    }

    public static IEnumerable<object[]> ForeignCoverageCases()
    {
        yield return ["exact Java/TCP/port/profile", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r), FirewallRuleCoverage.ExactEquivalent];
        yield return ["any program exact port", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ApplicationName = "" }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["any program any protocol any port", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ApplicationName = "", Protocol = WindowsFirewallPolicy.ProtocolAny, LocalPorts = "" }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["AppContainer package", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ApplicationName = "", LocalAppPackageId = "S-1-15-2-fixture" }), FirewallRuleCoverage.ConstrainedDoesNotMatch];
        yield return ["local user owner", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ApplicationName = "", LocalUserOwner = "S-1-5-21-fixture" }), FirewallRuleCoverage.UnknownOrUnsupported];
        yield return ["local authorized users", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalUserAuthorizedList = "D:(A;;CC;;;S-1-5-21-fixture)" }), FirewallRuleCoverage.UnknownOrUnsupported];
        yield return ["remote authorized users", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteUserAuthorizedList = "D:(A;;CC;;;S-1-5-21-fixture)" }), FirewallRuleCoverage.UnknownOrUnsupported];
        yield return ["remote authorized machines", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteMachineAuthorizedList = "D:(A;;CC;;;S-1-5-21-fixture)" }), FirewallRuleCoverage.UnknownOrUnsupported];
        yield return ["IPsec security", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { SecureFlags = 2 }), FirewallRuleCoverage.UnknownOrUnsupported];
        yield return ["unrelated service", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { ServiceName = "OtherService" }), FirewallRuleCoverage.ConstrainedDoesNotMatch];
        yield return ["excluding interface type", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { InterfaceTypes = "Wireless" }), FirewallRuleCoverage.ConstrainedDoesNotMatch];
        yield return ["including interface type", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { InterfaceTypes = "Lan" }), FirewallRuleCoverage.ConstrainedMatch];
        yield return ["excluding named interface", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Interfaces = ["Wi-Fi"] }), FirewallRuleCoverage.ConstrainedDoesNotMatch];
        yield return ["including named interface", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Interfaces = ["Ethernet"] }), FirewallRuleCoverage.ConstrainedMatch];
        yield return ["excluding local address", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalAddresses = "10.10.10.10" }), FirewallRuleCoverage.ConstrainedDoesNotMatch];
        yield return ["including local address range", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalAddresses = "192.168.1.1-192.168.1.100" }), FirewallRuleCoverage.ConstrainedMatch];
        yield return ["containing port range", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalPorts = "25560-25570" }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["excluding port range", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { LocalPorts = "25500-25510" }), FirewallRuleCoverage.DoesNotMatch];
        yield return ["any protocol", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Protocol = WindowsFirewallPolicy.ProtocolAny }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["all profiles", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Profiles = FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["disabled", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Enabled = false }), FirewallRuleCoverage.DoesNotMatch];
        yield return ["outbound", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { Direction = FirewallRuleDirection.Outbound }), FirewallRuleCoverage.DoesNotMatch];
        yield return ["remote address restriction", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { RemoteAddresses = "203.0.113.10" }), FirewallRuleCoverage.ConstrainedMatch];
        yield return ["edge traversal", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { EdgeTraversal = true, EdgeTraversalOptions = 1 }), FirewallRuleCoverage.BroadUnrestricted];
        yield return ["unreadable package condition", new Func<FirewallRuleSnapshot, FirewallRuleSnapshot>(r => r with { UnavailableFields = FirewallRuleUnavailableFields.LocalAppPackageId }), FirewallRuleCoverage.UnknownOrUnsupported];
    }

    [Theory]
    [MemberData(nameof(ForeignCoverageCases))]
    public void Foreign_rule_coverage_is_explicit_and_conservative(
        string _, Func<FirewallRuleSnapshot, FirewallRuleSnapshot> change, FirewallRuleCoverage expected)
    {
        var plan = Plan();
        Assert.Equal(expected, WindowsFirewallPolicy.EvaluateRuleCoverage(plan, change(Exact(plan))).Coverage);
    }

    [Fact]
    public void Unknown_potential_block_fails_closed_but_unrelated_AppContainer_block_does_not()
    {
        var plan = Plan();
        var exact = Exact(plan);
        var unknown = exact with
        {
            Name = "Unknown conditional block",
            Action = FirewallRuleAction.Block,
            UnavailableFields = FirewallRuleUnavailableFields.LocalUserAuthorizedList
        };
        var unrelatedPackage = exact with
        {
            Name = "Packaged app block",
            Action = FirewallRuleAction.Block,
            ApplicationName = "",
            LocalAppPackageId = "S-1-15-2-fixture"
        };

        Assert.Same(unknown,
            WindowsFirewallPolicy.FindPotentiallyApplicableUnknownBlockRule(plan, [unknown]));
        Assert.Null(WindowsFirewallPolicy.FindApplicableBlockRule(plan, [unknown]));
        Assert.Null(WindowsFirewallPolicy.FindApplicableBlockRule(plan, [unrelatedPackage]));
        Assert.Null(WindowsFirewallPolicy.FindPotentiallyApplicableUnknownBlockRule(plan, [unrelatedPackage]));
    }

    [Theory]
    [InlineData("java.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Java\java*.exe")]
    public void Untrusted_runtime_is_rejected(string path) =>
        Assert.False(WindowsFirewallPolicy.IsTrustworthyJavaRuntime(path, out _));

    [Fact]
    public void Target_resolver_pairs_the_real_lan_not_the_vpn()
    {
        var vpn = Interface("vpn", "ExpressVPN WinTUN", "10.8.0.2", true, true);
        var lan = Interface("{11111111-1111-1111-1111-111111111111}", "Ethernet", "192.168.1.20", true, true);
        var resolver = new WindowsFirewallTargetResolver(
            new NetworkView(vpn, lan),
            new CategoryView(
                new NetworkCategoryBinding { AdapterId = vpn.Id, Connected = true, Category = WindowsNetworkCategory.Public, NetworkName = "VPN" },
                new NetworkCategoryBinding { AdapterId = lan.Id, Connected = true, Category = WindowsNetworkCategory.Private, NetworkName = "Home" }),
            _ => true);

        var result = resolver.Resolve(Java, "managed runtime", 25566);

        Assert.True(result.Resolved);
        Assert.Equal(FirewallProfile.Private, result.Profile);
        Assert.Equal("Ethernet", result.InterfaceName);
        Assert.Equal(25566, result.Port);
    }

    [Fact]
    public void Helper_parser_refuses_arbitrary_surface_udp_and_multiple_profiles()
    {
        var valid = Command(FirewallHelperOperation.Create);
        Assert.True(FirewallHelperCommandParser.Parse(FirewallHelperCommandParser.ToArguments(valid)).Valid);
        Assert.False(FirewallHelperCommandParser.Parse(["--powershell", "whoami"]).Valid);
        Assert.False(FirewallHelperCommandParser.Parse(FirewallHelperCommandParser.ToArguments(
            valid with { Transport = MappingTransport.Udp })).Valid);
        Assert.False(FirewallHelperCommandParser.Parse(FirewallHelperCommandParser.ToArguments(
            valid with { Profiles = FirewallProfile.Private | FirewallProfile.Public })).Valid);
    }

    [Fact]
    public void Helper_create_update_remove_are_owned_exact_and_idempotent()
    {
        var backend = new MemoryBackend();
        var create = Command(FirewallHelperOperation.Create);
        Assert.Equal(FirewallHelperExitCode.Applied, FirewallHelperRunner.Run(create, backend).ExitCode);
        Assert.Single(backend.Rules);
        Assert.Empty(WindowsFirewallPolicy.Differences(create.ToPlan(), backend.Rules.Single()));

        var update = create with { Operation = FirewallHelperOperation.Update, Port = 25567 };
        Assert.Equal(FirewallHelperExitCode.Applied, FirewallHelperRunner.Run(update, backend).ExitCode);
        Assert.Single(backend.Rules);
        Assert.Equal("25567", backend.Rules.Single().LocalPorts);

        var remove = create with { Operation = FirewallHelperOperation.Remove, ProgramPath = "", Port = 0 };
        Assert.Equal(FirewallHelperExitCode.Applied, FirewallHelperRunner.Run(remove, backend).ExitCode);
        Assert.Empty(backend.Rules);
        Assert.Equal(FirewallHelperExitCode.NothingToRemove, FirewallHelperRunner.Run(remove, backend).ExitCode);
    }

    [Fact]
    public void Helper_never_overwrites_or_removes_a_foreign_collision()
    {
        var command = Command(FirewallHelperOperation.Create);
        var foreign = Exact(command.ToPlan()) with { Grouping = "Foreign", Description = "Foreign" };
        var backend = new MemoryBackend(foreign);

        Assert.Equal(FirewallHelperExitCode.OwnershipConflict,
            FirewallHelperRunner.Run(command, backend).ExitCode);
        Assert.Equal(FirewallHelperExitCode.OwnershipConflict,
            FirewallHelperRunner.Run(command with { Operation = FirewallHelperOperation.Remove }, backend).ExitCode);
        Assert.Same(foreign, backend.Rules.Single());
    }

    [Fact]
    public void Helper_locator_uses_only_fixed_app_or_sibling_paths()
    {
        var app = @"D:\ChunkPilot\src\ChunkPilot.App\bin\Release\net10.0-windows\";
        var expected = Path.GetFullPath(Path.Combine(app, "..", "..", "..", "..",
            "ChunkPilot.FirewallHelper", "bin", "Release", "net10.0-windows",
            FirewallHelperLocator.HelperFileName));

        Assert.Equal(expected, FirewallHelperLocator.Resolve(app, path => path == expected));
        Assert.Null(FirewallHelperLocator.Resolve(app, path => path == @"C:\untrusted\ChunkPilot.FirewallHelper.exe"));
    }

    [Fact]
    public async Task Missing_elevation_helper_fails_closed_without_starting_a_process()
    {
        var launcher = new ShellElevationLauncher(() => null);

        var outcome = await launcher.LaunchAsync(["--operation", "create"], CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.False(outcome.Cancelled);
        Assert.Contains("not found", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Combined_copy_never_claims_external_reachability()
    {
        var firewall = new WindowsFirewallState { Phase = FirewallAccessPhase.Configured };
        var router = new RouterMappingState { Phase = RouterMappingPhase.Active };
        var text = WindowsFirewallPresentation.CombinedStatus(router, firewall);

        Assert.Contains("not been verified", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("friends can connect", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicly reachable", text, StringComparison.OrdinalIgnoreCase);
    }

    private static FirewallRulePlan Plan() => new()
    {
        ServerId = ServerId,
        RuleId = RuleId,
        ProgramPath = Java,
        Port = 25566,
        Transport = MappingTransport.Tcp,
        Profiles = FirewallProfile.Private,
        TargetLocalAddress = "192.168.1.50",
        TargetInterfaceName = "Ethernet",
        TargetInterfaceType = "Lan"
    };

    private static FirewallHelperCommand Command(FirewallHelperOperation operation) => new()
    {
        Operation = operation,
        OperationId = Guid.NewGuid(),
        ServerId = ServerId,
        RuleId = RuleId,
        ProgramPath = Java,
        Port = 25566,
        Transport = MappingTransport.Tcp,
        Profiles = FirewallProfile.Private
    };

    private static FirewallAccessRecord Record(FirewallRulePlan plan) => new()
    {
        ServerId = plan.ServerId,
        Configured = true,
        RuleId = plan.RuleId,
        RuleName = plan.RuleName,
        ProgramPath = plan.ProgramPath,
        Port = plan.Port,
        Profiles = plan.Profiles
    };

    private static FirewallRuleSnapshot Exact(FirewallRulePlan plan) => new()
    {
        Name = plan.RuleName,
        Description = plan.Description,
        Grouping = plan.Grouping,
        Enabled = true,
        Direction = plan.Direction,
        Action = plan.Action,
        Protocol = plan.Protocol,
        LocalPorts = plan.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RemotePorts = "*",
        LocalAddresses = plan.LocalAddresses,
        RemoteAddresses = plan.RemoteAddresses,
        ApplicationName = plan.ProgramPath,
        Profiles = plan.Profiles,
        InterfaceTypes = plan.InterfaceTypes,
        EdgeTraversal = false
    };

    private static LanInterfaceCandidate Interface(
        string id, string name, string address, bool gateway, bool defaultRoute) => new(
        id, name, name, NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1_000_000_000,
        gateway, defaultRoute, [new LanAddressCandidate(IPAddress.Parse(address), 24)]);

    private sealed class NetworkView(params LanInterfaceCandidate[] interfaces) : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
            interfaces.Select(item => new RouterGatewayCandidate(item, [IPAddress.Parse("192.168.1.1")])).ToArray();
    }

    private sealed class CategoryView(params NetworkCategoryBinding[] bindings) : INetworkCategoryView
    {
        public IReadOnlyList<NetworkCategoryBinding> Enumerate() => bindings;
    }

    private sealed class MemoryBackend(params FirewallRuleSnapshot[] initial) : IFirewallMutationBackend
    {
        private readonly List<FirewallRuleSnapshot> rules = [.. initial];
        public FirewallBackendStatus Status { get; set; } = FirewallBackendStatus.Available;
        public IReadOnlyList<FirewallRuleSnapshot> Rules => rules;
        public IReadOnlyList<FirewallRuleSnapshot> ReadRules() => rules.ToArray();
        public void AddRule(FirewallRulePlan plan)
        {
            rules.RemoveAll(item => item.Name == plan.RuleName);
            rules.Add(Exact(plan));
        }
        public void RemoveRule(string ruleName) => rules.RemoveAll(item => item.Name == ruleName);
    }
}
