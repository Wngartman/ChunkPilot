using System.Collections;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class NetFwPolicyReaderTests
{
    [Fact]
    public void Complete_public_policy_read_preserves_every_required_fact()
    {
        var accessor = new FakeAccessor();
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.Equal(FirewallPlatformStatus.Available, snapshot.PlatformStatus);
        Assert.Equal(FirewallProfile.Public, snapshot.CurrentProfiles);
        Assert.True(snapshot.EnabledProfiles.HasFlag(FirewallProfile.Public));
        Assert.Equal(FirewallPolicyModifyState.Ok, snapshot.ModifyState);
        Assert.Equal(FirewallProfile.None, snapshot.BlockAllInboundProfiles);
        Assert.Equal(FirewallPolicyUnavailableFields.None, snapshot.UnavailableFields);
        Assert.True(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
        Assert.Single(snapshot.Rules);
    }

    [Fact]
    public void Activation_failure_is_a_genuine_platform_failure()
    {
        var snapshot = new NetFwPolicyReader(
            () => throw Failure("activation failed", unchecked((int)0x80040154))).Read();

        Assert.False(snapshot.Available);
        Assert.Equal(FirewallPlatformStatus.ReadFailed, snapshot.PlatformStatus);
        Assert.Contains("activation failed", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firewall_enabled_failure_keeps_platform_profile_and_other_policy_evidence()
    {
        var accessor = new FakeAccessor { FirewallEnabledFailureProfile = FirewallProfile.Public };
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.Equal(FirewallPlatformStatus.Available, snapshot.PlatformStatus);
        Assert.Equal(FirewallProfile.Public, snapshot.CurrentProfiles);
        Assert.Equal(FirewallPolicyModifyState.Ok, snapshot.ModifyState);
        Assert.True(snapshot.UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.FirewallEnabled));
        Assert.Equal(FirewallProfile.Public, snapshot.FirewallEnabledUnavailableProfiles);
        Assert.False(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
        Assert.True(snapshot.HasCompleteMutationEvidence(FirewallProfile.Private));
    }

    [Fact]
    public void Current_profile_failure_keeps_enabled_policy_and_rule_evidence()
    {
        var accessor = new FakeAccessor { FailCurrentProfiles = true };
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.Equal(FirewallPlatformStatus.Available, snapshot.PlatformStatus);
        Assert.Equal(FirewallProfile.None, snapshot.CurrentProfiles);
        Assert.True(snapshot.EnabledProfiles.HasFlag(FirewallProfile.Public));
        Assert.Equal(FirewallPolicyModifyState.Ok, snapshot.ModifyState);
        Assert.True(snapshot.UnavailableFields.HasFlag(
            FirewallPolicyUnavailableFields.CurrentProfiles));
        Assert.Single(snapshot.Rules);
        Assert.False(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
    }

    [Fact]
    public void Local_policy_failure_keeps_known_platform_profile_and_firewall_state()
    {
        var accessor = new FakeAccessor { FailModifyState = true };
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.Equal(FirewallProfile.Public, snapshot.CurrentProfiles);
        Assert.True(snapshot.EnabledProfiles.HasFlag(FirewallProfile.Public));
        Assert.Equal(FirewallPolicyModifyState.Unknown, snapshot.ModifyState);
        Assert.True(snapshot.UnavailableFields.HasFlag(
            FirewallPolicyUnavailableFields.LocalPolicyModifyState));
        Assert.Single(snapshot.Rules);
        Assert.False(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
    }

    [Fact]
    public void Block_all_inbound_failure_keeps_other_independent_evidence()
    {
        var accessor = new FakeAccessor { BlockAllFailureProfile = FirewallProfile.Public };
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.True(snapshot.EnabledProfiles.HasFlag(FirewallProfile.Public));
        Assert.Equal(FirewallPolicyModifyState.Ok, snapshot.ModifyState);
        Assert.Equal(FirewallProfile.Public, snapshot.BlockAllInboundUnavailableProfiles);
        Assert.True(snapshot.UnavailableFields.HasFlag(
            FirewallPolicyUnavailableFields.BlockAllInboundTraffic));
        Assert.False(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
    }

    [Fact]
    public void Rule_enumeration_failure_keeps_policy_facts_but_makes_absence_unsafe()
    {
        var accessor = new FakeAccessor { RulesComplete = false };
        var snapshot = new NetFwPolicyReader(() => accessor).Read();

        Assert.True(snapshot.Available);
        Assert.Equal(FirewallProfile.Public, snapshot.CurrentProfiles);
        Assert.True(snapshot.EnabledProfiles.HasFlag(FirewallProfile.Public));
        Assert.Equal(FirewallPolicyModifyState.Ok, snapshot.ModifyState);
        Assert.True(snapshot.UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.Rules));
        Assert.False(snapshot.RulesAvailable);
        Assert.False(snapshot.HasCompleteMutationEvidence(FirewallProfile.Public));
    }

    [Fact]
    public void Rule_enumeration_accepts_the_clr_ienumerator_projection()
    {
        var first = new object();
        var second = new object();
        var result = ReflectionNetFwPolicyAccessor.Enumerate(
            new EnumeratorCollection(first, second));

        Assert.True(result.Complete);
        Assert.Equal([first, second], result.Items.ToArray());
    }

    [Fact]
    public void Rule_reader_models_INetFwRule2_and_INetFwRule3_conditions()
    {
        var snapshot = ReflectionNetFwPolicyAccessor.ReadRule(new AdobeStyleRule());

        Assert.Equal("Adobe Native Client", snapshot.Name);
        Assert.Equal(WindowsFirewallPolicy.ProtocolAny, snapshot.Protocol);
        Assert.Equal(FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public,
            snapshot.Profiles);
        Assert.True(snapshot.EdgeTraversal);
        Assert.Equal(1, snapshot.EdgeTraversalOptions);
        Assert.Equal("S-1-5-21-111-222-333-1002", snapshot.LocalUserOwner);
        Assert.Equal("S-1-15-2-fixture", snapshot.LocalAppPackageId);
        Assert.Equal("local-users", snapshot.LocalUserAuthorizedList);
        Assert.Equal("remote-users", snapshot.RemoteUserAuthorizedList);
        Assert.Equal("remote-machines", snapshot.RemoteMachineAuthorizedList);
        Assert.Equal(2, snapshot.SecureFlags);
        Assert.Equal(["Ethernet"], snapshot.Interfaces);
        Assert.Equal(FirewallRuleUnavailableFields.None, snapshot.UnavailableFields);
    }

    [Fact]
    public void Missing_INetFwRule3_property_is_preserved_as_unknown_instead_of_dropping_rule()
    {
        var snapshot = ReflectionNetFwPolicyAccessor.ReadRule(new LegacyRule());

        Assert.Equal("Legacy fixture", snapshot.Name);
        Assert.True(snapshot.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.LocalAppPackageId));
        Assert.True(snapshot.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.LocalUserOwner));
        Assert.True(snapshot.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.SecureFlags));
        Assert.False(snapshot.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Name));
    }

    private static InvalidCastException Failure(string message, int hresult = unchecked((int)0x80004002)) =>
        new($"{message} (fixture HRESULT 0x{hresult:X8})");

    private sealed class FakeAccessor : INetFwPolicyAccessor
    {
        public FirewallProfile FirewallEnabledFailureProfile { get; init; }
        public FirewallProfile BlockAllFailureProfile { get; init; }
        public bool FailCurrentProfiles { get; init; }
        public bool FailModifyState { get; init; }
        public bool RulesComplete { get; init; } = true;

        public int CurrentProfileTypes => FailCurrentProfiles
            ? throw Failure("CurrentProfileTypes fixture failure")
            : (int)FirewallProfile.Public;

        public bool FirewallEnabled(int profile) =>
            FirewallEnabledFailureProfile.HasFlag((FirewallProfile)profile)
                ? throw Failure($"FirewallEnabled({profile}) fixture failure")
                : true;

        public bool BlockAllInboundTraffic(int profile) =>
            BlockAllFailureProfile.HasFlag((FirewallProfile)profile)
                ? throw Failure($"BlockAllInboundTraffic({profile}) fixture failure")
                : false;

        public int LocalPolicyModifyState => FailModifyState
            ? throw Failure("LocalPolicyModifyState fixture failure")
            : (int)FirewallPolicyModifyState.Ok;

        public NetFwRuleReadResult ReadRules() => new(
            [new FirewallRuleSnapshot { Name = "Fixture rule" }],
            RulesComplete,
            RulesComplete ? "" : "Rules fixture failure.");

        public void Dispose() { }
    }

    private sealed class EnumeratorCollection(params object[] items)
    {
        public IEnumerator _NewEnum => ((IEnumerable)items).GetEnumerator();
    }

    private class LegacyRule
    {
        public string Name => "Legacy fixture";
        public string Description => "fixture";
        public string Grouping => "fixture";
        public bool Enabled => true;
        public int Direction => (int)FirewallRuleDirection.Inbound;
        public int Action => (int)FirewallRuleAction.Allow;
        public int Protocol => WindowsFirewallPolicy.ProtocolTcp;
        public string LocalPorts => "25565";
        public string RemotePorts => "*";
        public string IcmpTypesAndCodes => "";
        public string LocalAddresses => "*";
        public string RemoteAddresses => "*";
        public string ApplicationName => @"D:\ChunkPilot\java\bin\java.exe";
        public string ServiceName => "";
        public int Profiles => (int)FirewallProfile.Public;
        public string[] Interfaces => [];
        public string InterfaceTypes => "All";
        public bool EdgeTraversal => false;
    }

    private sealed class AdobeStyleRule : LegacyRule
    {
        public new string Name => "Adobe Native Client";
        public new int Protocol => WindowsFirewallPolicy.ProtocolAny;
        public new string LocalPorts => "";
        public new string ApplicationName => "";
        public new int Profiles => (int)(FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public);
        public new string[] Interfaces => ["Ethernet"];
        public new bool EdgeTraversal => true;
        public int EdgeTraversalOptions => 1;
        public string LocalAppPackageId => "S-1-15-2-fixture";
        public string LocalUserOwner => "S-1-5-21-111-222-333-1002";
        public string LocalUserAuthorizedList => "local-users";
        public string RemoteUserAuthorizedList => "remote-users";
        public string RemoteMachineAuthorizedList => "remote-machines";
        public int SecureFlags => 2;
    }
}
