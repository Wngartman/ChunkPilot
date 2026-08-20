using System.Globalization;
using System.Net;

namespace ChunkPilot.Core;

/// <summary>Which Windows Firewall profile a rule belongs to.</summary>
/// <remarks>
/// The values are the documented <c>NET_FW_PROFILE_TYPE2</c> constants (icftypes.h): DOMAIN 0x1,
/// PRIVATE 0x2, PUBLIC 0x4. <c>NET_FW_PROFILE2_ALL</c> (0x7fffffff) is deliberately not modelled as a
/// value ChunkPilot can ask for — a rule assigned to every profile is exactly what this feature must
/// never create.
/// </remarks>
[Flags]
public enum FirewallProfile
{
    None = 0,
    Domain = 0x1,
    Private = 0x2,
    Public = 0x4
}

/// <summary>Direction of traffic a rule applies to (<c>NET_FW_RULE_DIRECTION</c>).</summary>
public enum FirewallRuleDirection
{
    Unknown = 0,
    Inbound = 1,
    Outbound = 2
}

/// <summary>What a rule does with matching traffic (<c>NET_FW_ACTION</c>).</summary>
public enum FirewallRuleAction
{
    Block = 0,
    Allow = 1
}

/// <summary>
/// Whether adding or changing a rule will actually take effect (<c>NET_FW_MODIFY_STATE</c>).
/// </summary>
/// <remarks>
/// This is the difference between a rule object existing and a rule mattering. Windows answers it
/// directly, so ChunkPilot never has to guess, and never reports success when Windows has said the
/// change will be ignored.
/// </remarks>
public enum FirewallPolicyModifyState
{
    /// <summary>Could not be read.</summary>
    Unknown = -1,
    /// <summary>Changing or adding a rule in the current profile will take effect.</summary>
    Ok = 0,
    /// <summary>The profile is controlled by Group Policy; a local change will not take effect.</summary>
    GroupPolicyOverride = 1,
    /// <summary>Unsolicited inbound traffic is not allowed, so an allow rule will not take effect.</summary>
    InboundBlocked = 2
}

/// <summary>Why the Windows Firewall policy reader could or could not provide authoritative state.</summary>
public enum FirewallPlatformStatus
{
    Available,
    UnsupportedPlatform,
    ApiUnavailable,
    AccessDenied,
    ReadFailed
}

/// <summary>Which independently read Windows Firewall policy facts could not be established.</summary>
[Flags]
public enum FirewallPolicyUnavailableFields
{
    None = 0,
    CurrentProfiles = 1 << 0,
    FirewallEnabled = 1 << 1,
    LocalPolicyModifyState = 1 << 2,
    BlockAllInboundTraffic = 1 << 3,
    Rules = 1 << 4
}

/// <summary>Why Network List Manager could or could not provide connected profile state.</summary>
public enum NetworkListStatus
{
    Available,
    UnsupportedPlatform,
    ApiUnavailable,
    AccessDenied,
    ReadFailed
}

/// <summary>Whether the shared router/LAN selection produced one trustworthy path.</summary>
public enum NetworkPathStatus
{
    Available,
    Unavailable,
    Ambiguous
}

/// <summary>The single primary condition the App should explain to a beginner.</summary>
public enum FirewallDiagnosticReason
{
    None,
    NotChecked,
    PublicProfileApprovalRequired,
    NetworkProfileUnavailable,
    NetworkProfileAmbiguous,
    NetworkPathUnavailable,
    NetworkPathAmbiguous,
    NetworkListUnavailable,
    FirewallPlatformUnavailable,
    FirewallPolicyIncomplete,
    FirewallProfileDisabled,
    LocalPolicyManaged,
    LocalPolicyReadOnly,
    ExplicitBlockConflict,
    UnknownBlockConstraint,
    ExistingForeignAllow,
    OwnedRuleStale,
    OwnershipConflict,
    JavaRuntimeUnresolved,
    PortUnresolved,
    ElevationCancelled,
    ElevationDenied,
    HelperLaunchFailed,
    HelperMutationRejected,
    RuleVerificationFailed,
    RemovalFailed,
    FirewallStateUnknown
}

public enum FirewallDiagnosticSeverity { Neutral, Info, Success, Warning, Error }

/// <summary>Actions the App already knows how to execute safely.</summary>
public enum FirewallRecoveryAction
{
    None,
    CheckAgain,
    Configure,
    ConfirmPublic,
    Update,
    RetryRemoval,
    ReviewJava,
    ReviewPort,
    ViewDetails,
    TryAgain
}

public enum FirewallElevationFailure
{
    None,
    HelperUnavailable,
    PermissionDenied,
    LaunchFailed
}

/// <summary>Typed diagnosis derived from authoritative evidence, never from presentation strings.</summary>
public sealed record FirewallDiagnostic
{
    public FirewallDiagnosticReason Reason { get; init; } = FirewallDiagnosticReason.NotChecked;
    public FirewallDiagnosticSeverity Severity { get; init; } = FirewallDiagnosticSeverity.Neutral;
    public FirewallRecoveryAction PrimaryAction { get; init; }
    public FirewallRecoveryAction SecondaryAction { get; init; }
    public bool AutomaticRecoveryAllowed { get; init; }
    public bool UacRequired { get; init; }
    public bool Retryable { get; init; }
}

/// <summary>How Windows classifies a network (<c>NLM_NETWORK_CATEGORY</c>).</summary>
public enum WindowsNetworkCategory
{
    Unknown = -1,
    Public = 0,
    Private = 1,
    DomainAuthenticated = 2
}

/// <summary>Who owns the rule a state is describing.</summary>
public enum FirewallRuleOwner
{
    /// <summary>No rule covers this server's traffic.</summary>
    None,
    /// <summary>A rule ChunkPilot created and can prove it created.</summary>
    ChunkPilot,
    /// <summary>A rule somebody else created. ChunkPilot reports it and never touches it.</summary>
    ExistingWindowsRule
}

/// <summary>How confidently one foreign rule covers the exact Minecraft traffic ChunkPilot needs.</summary>
public enum FirewallRuleCoverage
{
    None,
    ExactEquivalent,
    BroadUnrestricted,
    ConstrainedMatch,
    ConstrainedDoesNotMatch,
    UnknownOrUnsupported,
    DoesNotMatch
}

/// <summary>Documented rule properties that Windows did not expose for one enumerated rule.</summary>
[Flags]
public enum FirewallRuleUnavailableFields
{
    None = 0,
    Name = 1 << 0,
    Description = 1 << 1,
    Grouping = 1 << 2,
    Enabled = 1 << 3,
    Direction = 1 << 4,
    Action = 1 << 5,
    Protocol = 1 << 6,
    LocalPorts = 1 << 7,
    RemotePorts = 1 << 8,
    IcmpTypesAndCodes = 1 << 9,
    LocalAddresses = 1 << 10,
    RemoteAddresses = 1 << 11,
    ApplicationName = 1 << 12,
    ServiceName = 1 << 13,
    Profiles = 1 << 14,
    Interfaces = 1 << 15,
    InterfaceTypes = 1 << 16,
    EdgeTraversal = 1 << 17,
    EdgeTraversalOptions = 1 << 18,
    LocalAppPackageId = 1 << 19,
    LocalUserOwner = 1 << 20,
    LocalUserAuthorizedList = 1 << 21,
    RemoteUserAuthorizedList = 1 << 22,
    RemoteMachineAuthorizedList = 1 << 23,
    SecureFlags = 1 << 24
}

/// <summary>The semantic result of comparing one foreign rule with one exact Minecraft target.</summary>
public sealed record FirewallRuleEvaluation(
    FirewallRuleSnapshot Rule,
    FirewallRuleCoverage Coverage)
{
    public bool ProvesExactEquivalent => Coverage == FirewallRuleCoverage.ExactEquivalent;
    public bool ProvesDoesNotMatch => Coverage is FirewallRuleCoverage.DoesNotMatch or
        FirewallRuleCoverage.ConstrainedDoesNotMatch;
    public bool MayApply => !ProvesDoesNotMatch && Coverage != FirewallRuleCoverage.None;
}

/// <summary>
/// The one authoritative state a server's Windows Firewall access can be in. The App renders exactly
/// these and never derives a state of its own.
/// </summary>
public enum FirewallAccessPhase
{
    /// <summary>Nothing has been read yet.</summary>
    NotChecked,
    /// <summary>A read of Windows Firewall is running.</summary>
    Checking,
    /// <summary>The firewall API is not available on this machine right now.</summary>
    Unsupported,
    /// <summary>Windows Firewall is switched off for the profile this server's network uses.</summary>
    FirewallDisabled,
    /// <summary>ChunkPilot could not establish a trustworthy Java runtime, so no rule may be created.</summary>
    RuntimeUnavailable,
    /// <summary>The server has no valid authoritative listen port yet.</summary>
    PortUnavailable,
    /// <summary>The routed LAN adapter could not be correlated to one Windows network profile.</summary>
    NetworkUnavailable,
    /// <summary>Nothing allows this server's traffic yet. The user may ask ChunkPilot to configure it.</summary>
    NeedsPermission,
    /// <summary>A rule ChunkPilot did not create already covers this exact traffic.</summary>
    ExistingWindowsRule,
    /// <summary>The selected network is Public. A separate, explicit approval is required.</summary>
    PublicNetworkConfirmationRequired,
    /// <summary>The elevation prompt is open and Windows is waiting for the user.</summary>
    WaitingForElevation,
    /// <summary>The privileged one-shot operation is applying.</summary>
    Configuring,
    /// <summary>A removal is applying.</summary>
    Removing,
    /// <summary>ChunkPilot's rule exists and every property was verified against Windows.</summary>
    Configured,
    /// <summary>ChunkPilot's rule exists but no longer matches the server. An explicit update is needed.</summary>
    Stale,
    /// <summary>An explicit block rule applies to this traffic. ChunkPilot will not touch it.</summary>
    BlockedByPolicy,
    /// <summary>Local firewall changes will not take effect on this machine.</summary>
    ManagedByOrganization,
    /// <summary>A rule with ChunkPilot's identifier exists that ChunkPilot cannot prove is its own.</summary>
    OwnershipConflict,
    /// <summary>Something failed, or an owned rule disappeared. Nothing is claimed to be working.</summary>
    NeedsAttention
}

/// <summary>Why an operation did not produce the intended firewall state. Drives plain-language copy.</summary>
public enum FirewallAccessFailure
{
    None,
    /// <summary>The user dismissed the elevation prompt. Not an error, and never a retry loop.</summary>
    Cancelled,
    /// <summary>The privileged helper could not be found in the trusted installation location.</summary>
    HelperUnavailable,
    /// <summary>Windows refused to start the elevated helper for a reason other than cancellation.</summary>
    ElevationFailed,
    /// <summary>The helper ran but did not complete the operation.</summary>
    HelperFailed,
    /// <summary>Windows refused the change for permission reasons.</summary>
    AccessDenied,
    /// <summary>Group Policy or an inbound-blocked profile prevents a local rule taking effect.</summary>
    PolicyPrevented,
    /// <summary>A rule carrying ChunkPilot's identifier could not be proven to be ChunkPilot's.</summary>
    OwnershipConflict,
    /// <summary>No trustworthy Java executable could be established for this server.</summary>
    RuntimeUnavailable,
    /// <summary>The server has no usable port.</summary>
    PortUnavailable,
    /// <summary>No trustworthy local network could be correlated with a Windows firewall profile.</summary>
    NetworkUnavailable,
    /// <summary>The Windows Firewall service or API did not answer.</summary>
    ServiceUnavailable,
    /// <summary>The mutation reported success but Windows does not show the expected rule.</summary>
    VerificationFailed,
    /// <summary>A removal did not complete. The ownership evidence is kept so it can be retried.</summary>
    RemovalFailed,
    /// <summary>The authoritative target changed while the elevation prompt was open.</summary>
    TargetChanged,
    Unknown
}

/// <summary>One Windows Firewall rule, read as data. Nothing here is ChunkPilot-specific.</summary>
public sealed record FirewallRuleSnapshot
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Grouping { get; init; } = "";
    public bool Enabled { get; init; }
    public FirewallRuleDirection Direction { get; init; } = FirewallRuleDirection.Unknown;
    public FirewallRuleAction Action { get; init; } = FirewallRuleAction.Block;

    /// <summary>IP protocol number. 6 is TCP, 17 is UDP, 256 is "any" in the firewall API.</summary>
    public int Protocol { get; init; } = WindowsFirewallPolicy.ProtocolAny;

    public string LocalPorts { get; init; } = "";
    public string RemotePorts { get; init; } = "";
    public string IcmpTypesAndCodes { get; init; } = "";
    public string LocalAddresses { get; init; } = "";
    public string RemoteAddresses { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public FirewallProfile Profiles { get; init; } = FirewallProfile.None;

    /// <summary>True when the rule is assigned to every profile, including ones added later.</summary>
    public bool AppliesToAllProfiles { get; init; }

    public bool EdgeTraversal { get; init; }
    public int EdgeTraversalOptions { get; init; }
    public IReadOnlyList<string> Interfaces { get; init; } = [];
    public string InterfaceTypes { get; init; } = "";
    public string LocalAppPackageId { get; init; } = "";
    public string LocalUserOwner { get; init; } = "";
    public string LocalUserAuthorizedList { get; init; } = "";
    public string RemoteUserAuthorizedList { get; init; } = "";
    public string RemoteMachineAuthorizedList { get; init; } = "";
    public int SecureFlags { get; init; }
    public FirewallRuleUnavailableFields UnavailableFields { get; init; }
}

/// <summary>Everything ChunkPilot reads from Windows Firewall in one pass.</summary>
public sealed record FirewallPolicySnapshot
{
    /// <summary>False when the firewall API or service could not be reached at all.</summary>
    public bool Available { get; init; }
    public FirewallPlatformStatus PlatformStatus { get; init; } = FirewallPlatformStatus.Available;

    /// <summary>Profiles Windows reports as currently active. May legitimately be more than one.</summary>
    public FirewallProfile CurrentProfiles { get; init; } = FirewallProfile.None;

    /// <summary>Profiles for which the firewall itself is switched on.</summary>
    public FirewallProfile EnabledProfiles { get; init; } = FirewallProfile.None;

    /// <summary>Profiles for which Windows is set to block all inbound connections.</summary>
    public FirewallProfile BlockAllInboundProfiles { get; init; } = FirewallProfile.None;

    public FirewallPolicyModifyState ModifyState { get; init; } = FirewallPolicyModifyState.Unknown;
    public IReadOnlyList<FirewallRuleSnapshot> Rules { get; init; } = [];

    /// <summary>
    /// Policy fields that failed independently after the COM platform was created. These failures do
    /// not erase fields Windows already returned successfully.
    /// </summary>
    public FirewallPolicyUnavailableFields UnavailableFields { get; init; }

    /// <summary>Profiles whose <c>FirewallEnabled</c> value could not be read.</summary>
    public FirewallProfile FirewallEnabledUnavailableProfiles { get; init; }

    /// <summary>Profiles whose <c>BlockAllInboundTraffic</c> value could not be read.</summary>
    public FirewallProfile BlockAllInboundUnavailableProfiles { get; init; }

    public bool RulesAvailable => Available && !UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.Rules);

    /// <summary>
    /// True only when every read needed to safely create or update a rule for one exact profile is
    /// authoritative. Failures on unrelated inactive profiles do not block that exact-profile decision.
    /// </summary>
    public bool HasCompleteMutationEvidence(FirewallProfile profile) =>
        Available && profile is FirewallProfile.Domain or FirewallProfile.Private or FirewallProfile.Public &&
        !UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.CurrentProfiles) &&
        !UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.LocalPolicyModifyState) &&
        !UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.Rules) &&
        (!UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.FirewallEnabled) ||
         FirewallEnabledUnavailableProfiles != FirewallProfile.None &&
         !FirewallEnabledUnavailableProfiles.HasFlag(profile)) &&
        (!UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.BlockAllInboundTraffic) ||
         BlockAllInboundUnavailableProfiles != FirewallProfile.None &&
         !BlockAllInboundUnavailableProfiles.HasFlag(profile));

    public FirewallPolicyUnavailableFields UnavailableFieldsFor(FirewallProfile profile)
    {
        var fields = UnavailableFields & ~(FirewallPolicyUnavailableFields.FirewallEnabled |
                                           FirewallPolicyUnavailableFields.BlockAllInboundTraffic);
        if (UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.FirewallEnabled) &&
            (profile == FirewallProfile.None || FirewallEnabledUnavailableProfiles == FirewallProfile.None ||
             FirewallEnabledUnavailableProfiles.HasFlag(profile)))
            fields |= UnavailableFields & FirewallPolicyUnavailableFields.FirewallEnabled;
        if (UnavailableFields.HasFlag(FirewallPolicyUnavailableFields.BlockAllInboundTraffic) &&
            (profile == FirewallProfile.None || BlockAllInboundUnavailableProfiles == FirewallProfile.None ||
             BlockAllInboundUnavailableProfiles.HasFlag(profile)))
            fields |= UnavailableFields & FirewallPolicyUnavailableFields.BlockAllInboundTraffic;
        return fields;
    }

    /// <summary>Exact technical detail for the Technical details panel. Never primary copy.</summary>
    public string Detail { get; init; } = "";

    public static FirewallPolicySnapshot Unavailable(
        string detail, FirewallPlatformStatus status = FirewallPlatformStatus.ApiUnavailable) =>
        new() { Detail = detail, PlatformStatus = status };
}

/// <summary>One read-only NLM enumeration, including typed failure evidence.</summary>
public sealed record NetworkCategorySnapshot
{
    public NetworkListStatus Status { get; init; } = NetworkListStatus.Available;
    public IReadOnlyList<NetworkCategoryBinding> Bindings { get; init; } = [];
    public string Detail { get; init; } = "";
    public bool Available => Status == NetworkListStatus.Available;

    public static NetworkCategorySnapshot Unavailable(NetworkListStatus status, string detail) =>
        new() { Status = status, Detail = detail };
}

/// <summary>Which Windows network a real interface is attached to, and how Windows classifies it.</summary>
public sealed record NetworkCategoryBinding
{
    /// <summary>The adapter GUID, in the same form <see cref="System.Net.NetworkInformation.NetworkInterface.Id"/> uses.</summary>
    public string AdapterId { get; init; } = "";
    /// <summary>The Windows IP interface index for the adapter, or zero when unavailable.</summary>
    public int InterfaceIndex { get; init; }
    public string NetworkName { get; init; } = "";
    public WindowsNetworkCategory Category { get; init; } = WindowsNetworkCategory.Unknown;
    public bool Connected { get; init; }
}

/// <summary>Why a target could not be established. Each value has its own honest message.</summary>
public enum FirewallTargetProblem
{
    None,
    /// <summary>No trustworthy Java executable. A broader rule is never substituted.</summary>
    RuntimeUnavailable,
    PortUnavailable,
    NetworkPathUnavailable,
    NetworkPathAmbiguous,
    NetworkListUnavailable,
    NetworkProfileUnavailable,
    NetworkProfileAmbiguous
}

/// <summary>
/// The authoritative facts a firewall rule for one server would have to encode: which executable, which
/// port, which transport, and which single Windows profile.
/// </summary>
/// <remarks>
/// Resolved from ChunkPilot's own state — never from PATH, never from a launcher wrapper, never from
/// free text. When any part cannot be established the resolution fails rather than widening.
/// </remarks>
public sealed record FirewallTargetResolution
{
    public bool Resolved { get; init; }
    public string ProgramPath { get; init; } = "";
    public string RuntimeSource { get; init; } = "";
    public int Port { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;

    /// <summary>Exactly one profile, chosen from the selected LAN interface's own network category.</summary>
    public FirewallProfile Profile { get; init; } = FirewallProfile.None;

    public WindowsNetworkCategory Category { get; init; } = WindowsNetworkCategory.Unknown;
    public string NetworkName { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public string InterfaceType { get; init; } = "";
    public int InterfaceIndex { get; init; }
    public string LocalAddress { get; init; } = "";
    public string GatewayAddress { get; init; } = "";
    public NetworkPathStatus NetworkPathStatus { get; init; } = NetworkPathStatus.Unavailable;
    public NetworkListStatus NetworkListStatus { get; init; } = NetworkListStatus.Available;
    public string NetworkListDetail { get; init; } = "";
    public FirewallTargetProblem Problem { get; init; } = FirewallTargetProblem.None;
    public string Detail { get; init; } = "";

    public static FirewallTargetResolution Failed(FirewallTargetProblem problem, string detail) =>
        new() { Problem = problem, Detail = detail };
}

/// <summary>
/// The exact rule ChunkPilot intends. Every field is asserted after the mutation; a rule that differs
/// in any of them is not this plan and is never reported as configured.
/// </summary>
public sealed record FirewallRulePlan
{
    public Guid ServerId { get; init; }

    /// <summary>Minted per create or update. Identity never depends on a display name or a port.</summary>
    public Guid RuleId { get; init; }

    public string ProgramPath { get; init; } = "";
    public int Port { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;
    public FirewallProfile Profiles { get; init; } = FirewallProfile.None;
    public string TargetLocalAddress { get; init; } = "";
    public string TargetInterfaceName { get; init; } = "";
    public string TargetInterfaceType { get; init; } = "";

    public string RuleName => WindowsFirewallPolicy.RuleName(RuleId);
    public string Grouping => WindowsFirewallPolicy.RuleGroup;
    public string Description => WindowsFirewallPolicy.RuleDescription(RuleId, Port, Transport);
    public FirewallRuleDirection Direction => FirewallRuleDirection.Inbound;
    public FirewallRuleAction Action => FirewallRuleAction.Allow;
    public int Protocol => WindowsFirewallPolicy.ProtocolFor(Transport);

    /// <summary>
    /// Any remote address. Deliberately not <c>LocalSubnet</c>: Direct internet exists precisely so that
    /// legitimate clients outside the household can connect, and Microsoft's own guidance excludes the
    /// local-subnet restriction from services that require global internet connectivity.
    /// </summary>
    public string RemoteAddresses => WindowsFirewallPolicy.AnyAddress;

    public string LocalAddresses => WindowsFirewallPolicy.AnyAddress;
    public string InterfaceTypes => WindowsFirewallPolicy.AllInterfaceTypes;

    /// <summary>
    /// Off. Edge traversal exists to let Teredo-tunnelled IPv6 traffic reach a listener; a Java Edition
    /// server reached through an IPv4 router mapping has no use for it, and enabling it would widen the
    /// exposure beyond what Direct internet needs.
    /// </summary>
    public bool EdgeTraversal => false;
}

/// <summary>
/// Everything ChunkPilot durably remembers about one server's Windows Firewall access: the user's
/// deliberate configuration, and the minimum evidence needed to prove later that a rule is its own.
/// </summary>
/// <remarks>
/// A server with no row has no ChunkPilot-owned rule. That is the default for every server, including
/// every server that existed before this feature.
/// </remarks>
public sealed record FirewallAccessRecord
{
    public Guid ServerId { get; init; }

    /// <summary>
    /// ChunkPilot created a rule and has not removed it. Durable: it survives stops, starts, restarts,
    /// application exit and Agent restarts, because the rule itself does.
    /// </summary>
    public bool Configured { get; init; }

    /// <summary>Identity of the rule. Not the display name, not the port, not the Java path.</summary>
    public Guid RuleId { get; init; }

    /// <summary>The exact name written into Windows, kept so the rule can be found again.</summary>
    public string RuleName { get; init; } = "";

    public string ProgramPath { get; init; } = "";
    public int Port { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;
    public FirewallProfile Profiles { get; init; } = FirewallProfile.None;

    /// <summary>The user separately approved a Public-profile exception for this server.</summary>
    public bool PublicApproved { get; init; }
    public DateTimeOffset? PublicApprovedAt { get; init; }

    public DateTimeOffset? ConfiguredAt { get; init; }

    /// <summary>A removal that did not complete. Retained so it is retried rather than forgotten.</summary>
    public bool RemovalPending { get; init; }

    /// <summary>The server this rule belonged to was deleted before the rule could be withdrawn.</summary>
    public bool ServerRemoved { get; init; }

    public FirewallAccessFailure LastFailure { get; init; } = FirewallAccessFailure.None;
    public string LastOperationDetail { get; init; } = "";
    public DateTimeOffset? LastCheckedAt { get; init; }
}

/// <summary>The authoritative snapshot the App renders. The App adds copy, never state.</summary>
public sealed record WindowsFirewallState
{
    public Guid ServerId { get; init; }
    public FirewallAccessPhase Phase { get; init; } = FirewallAccessPhase.NotChecked;
    public FirewallAccessFailure Failure { get; init; } = FirewallAccessFailure.None;
    public FirewallDiagnostic Diagnostic { get; init; } = new();
    public FirewallRuleOwner Owner { get; init; } = FirewallRuleOwner.None;

    /// <summary>ChunkPilot holds ownership evidence for a rule right now.</summary>
    public bool Configured { get; init; }
    public bool RemovalPending { get; init; }

    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = "";
    public string RuleGroup { get; init; } = "";

    public string ProgramPath { get; init; } = "";
    public bool JavaVerified { get; init; }
    public string RuntimeSource { get; init; } = "";
    public int Port { get; init; }
    public bool PortVerified { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;

    /// <summary>The profiles the rule actually carries, or the ones the plan intends.</summary>
    public FirewallProfile Profiles { get; init; } = FirewallProfile.None;

    /// <summary>The profile the selected trusted LAN interface maps to. Exactly one, or none.</summary>
    public FirewallProfile SelectedProfile { get; init; } = FirewallProfile.None;
    public bool NetworkProfileVerified { get; init; }

    public WindowsNetworkCategory Category { get; init; } = WindowsNetworkCategory.Unknown;
    public string NetworkName { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public int InterfaceIndex { get; init; }
    public string LocalAddress { get; init; } = "";
    public string GatewayAddress { get; init; } = "";
    public NetworkPathStatus NetworkPathStatus { get; init; } = NetworkPathStatus.Unavailable;
    public NetworkListStatus NetworkListStatus { get; init; } = NetworkListStatus.Available;
    public FirewallTargetProblem TargetProblem { get; init; } = FirewallTargetProblem.None;

    public bool FirewallApiAvailable { get; init; }
    public FirewallPlatformStatus FirewallPlatformStatus { get; init; } = FirewallPlatformStatus.ApiUnavailable;
    public FirewallPolicyUnavailableFields FirewallPolicyUnavailableFields { get; init; }
    public bool FirewallEnabledForProfile { get; init; }
    public bool BlockAllInboundForProfile { get; init; }
    public FirewallPolicyModifyState ModifyState { get; init; } = FirewallPolicyModifyState.Unknown;

    /// <summary>An existing Windows allow rule that already covers this traffic, when there is one.</summary>
    public string ExistingRuleName { get; init; } = "";
    public FirewallRuleCoverage ExistingRuleCoverage { get; init; }

    /// <summary>A non-equivalent foreign allow detected for technical evidence only.</summary>
    public string OtherAllowRuleName { get; init; } = "";
    public FirewallRuleCoverage OtherAllowRuleCoverage { get; init; }

    /// <summary>An applicable explicit block rule, when one was detected.</summary>
    public string BlockingRuleName { get; init; } = "";
    public FirewallRuleCoverage BlockingRuleCoverage { get; init; }

    /// <summary>Why an owned rule is no longer exact. Empty when it is.</summary>
    public IReadOnlyList<string> StaleReasons { get; init; } = [];

    public DateTimeOffset? ConfiguredAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string TargetDetail { get; init; } = "";
    public string NetworkListDetail { get; init; } = "";
    public string FirewallPolicyDetail { get; init; } = "";
    public string LastOperationDetail { get; init; } = "";

    /// <summary>An operation owns this server right now; the App disables competing commands.</summary>
    public bool Busy { get; init; }
    public Guid OperationId { get; init; }

    /// <summary>
    /// A Public-profile exception has been separately approved for this server. Never inherited from
    /// another server and never assumed.
    /// </summary>
    public bool PublicApproved { get; init; }

    public static WindowsFirewallState NotChecked(Guid serverId) => new() { ServerId = serverId };
}

/// <summary>Deterministic primary-diagnosis priority over independently gathered evidence.</summary>
public static class WindowsFirewallDiagnostics
{
    public static FirewallDiagnostic Evaluate(WindowsFirewallState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase == FirewallAccessPhase.NotChecked)
            return Diagnostic(FirewallDiagnosticReason.NotChecked, FirewallDiagnosticSeverity.Neutral,
                FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Phase is FirewallAccessPhase.Checking or FirewallAccessPhase.WaitingForElevation or
            FirewallAccessPhase.Configuring or FirewallAccessPhase.Removing)
            return Diagnostic(FirewallDiagnosticReason.None, FirewallDiagnosticSeverity.Info);

        // Platform and effective-policy evidence outrank consent and target recovery because a local
        // rule cannot be truthfully offered when Windows says it cannot be read or cannot take effect.
        if (!state.FirewallApiAvailable || state.Phase == FirewallAccessPhase.Unsupported)
            return Diagnostic(FirewallDiagnosticReason.FirewallPlatformUnavailable,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.ModifyState == FirewallPolicyModifyState.GroupPolicyOverride)
            return Diagnostic(FirewallDiagnosticReason.LocalPolicyManaged,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ViewDetails);
        if (state.ModifyState == FirewallPolicyModifyState.InboundBlocked || state.BlockAllInboundForProfile)
            return Diagnostic(FirewallDiagnosticReason.LocalPolicyReadOnly,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ViewDetails);
        if (state.FirewallPolicyUnavailableFields != FirewallPolicyUnavailableFields.None)
            return Diagnostic(FirewallDiagnosticReason.FirewallPolicyIncomplete,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain,
                FirewallRecoveryAction.ViewDetails, retryable: true);
        if (state.Phase == FirewallAccessPhase.BlockedByPolicy)
            return Diagnostic(FirewallDiagnosticReason.ExplicitBlockConflict,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ViewDetails);
        if (state.BlockingRuleCoverage == FirewallRuleCoverage.UnknownOrUnsupported)
            return Diagnostic(FirewallDiagnosticReason.UnknownBlockConstraint,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain,
                FirewallRecoveryAction.ViewDetails, retryable: true);
        if (state.Phase == FirewallAccessPhase.OwnershipConflict ||
            state.Failure == FirewallAccessFailure.OwnershipConflict)
            return Diagnostic(FirewallDiagnosticReason.OwnershipConflict,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ViewDetails);

        if (state.Failure == FirewallAccessFailure.Cancelled)
            return Diagnostic(FirewallDiagnosticReason.ElevationCancelled,
                FirewallDiagnosticSeverity.Info, FirewallRecoveryAction.TryAgain, retryable: true, uac: true);
        if (state.Failure is FirewallAccessFailure.AccessDenied or FirewallAccessFailure.ElevationFailed)
            return Diagnostic(FirewallDiagnosticReason.ElevationDenied,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.TryAgain, retryable: true, uac: true);
        if (state.Failure == FirewallAccessFailure.HelperUnavailable)
            return Diagnostic(FirewallDiagnosticReason.HelperLaunchFailed,
                FirewallDiagnosticSeverity.Error, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Failure is FirewallAccessFailure.HelperFailed or FirewallAccessFailure.PolicyPrevented or
            FirewallAccessFailure.ServiceUnavailable)
            return Diagnostic(FirewallDiagnosticReason.HelperMutationRejected,
                FirewallDiagnosticSeverity.Error, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Failure == FirewallAccessFailure.VerificationFailed)
            return Diagnostic(FirewallDiagnosticReason.RuleVerificationFailed,
                FirewallDiagnosticSeverity.Error, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.RemovalPending || state.Failure == FirewallAccessFailure.RemovalFailed)
            return Diagnostic(FirewallDiagnosticReason.RemovalFailed,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.RetryRemoval, retryable: true, uac: true);

        // Target evidence is ordered by what prevents formation of the exact rule. Public consent is
        // deliberately evaluated later: Public is a known profile, not a target-resolution failure.
        if (state.Phase == FirewallAccessPhase.RuntimeUnavailable ||
            state.Failure == FirewallAccessFailure.RuntimeUnavailable)
            return Diagnostic(FirewallDiagnosticReason.JavaRuntimeUnresolved,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ReviewJava,
                FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Phase == FirewallAccessPhase.PortUnavailable ||
            state.Failure == FirewallAccessFailure.PortUnavailable)
            return Diagnostic(FirewallDiagnosticReason.PortUnresolved,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ReviewPort,
                FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.NetworkPathStatus == NetworkPathStatus.Ambiguous)
            return Diagnostic(FirewallDiagnosticReason.NetworkPathAmbiguous,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.NetworkPathStatus == NetworkPathStatus.Unavailable &&
            state.Phase == FirewallAccessPhase.NetworkUnavailable)
            return Diagnostic(FirewallDiagnosticReason.NetworkPathUnavailable,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.NetworkListStatus != NetworkListStatus.Available)
            return Diagnostic(FirewallDiagnosticReason.NetworkListUnavailable,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Phase == FirewallAccessPhase.NetworkUnavailable)
        {
            var reason = state.TargetProblem == FirewallTargetProblem.NetworkProfileAmbiguous
                ? FirewallDiagnosticReason.NetworkProfileAmbiguous
                : FirewallDiagnosticReason.NetworkProfileUnavailable;
            return Diagnostic(reason, FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain,
                retryable: true);
        }

        if (state.Phase == FirewallAccessPhase.FirewallDisabled)
            return Diagnostic(FirewallDiagnosticReason.FirewallProfileDisabled,
                FirewallDiagnosticSeverity.Info, FirewallRecoveryAction.CheckAgain, retryable: true);
        if (state.Phase == FirewallAccessPhase.Stale)
            return Diagnostic(FirewallDiagnosticReason.OwnedRuleStale,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.Update, retryable: true, uac: true);
        if (state.Phase == FirewallAccessPhase.ExistingWindowsRule)
            return Diagnostic(FirewallDiagnosticReason.ExistingForeignAllow,
                FirewallDiagnosticSeverity.Success);
        if (state.Phase == FirewallAccessPhase.Configured)
            return Diagnostic(FirewallDiagnosticReason.None, FirewallDiagnosticSeverity.Success);
        if (state.Phase == FirewallAccessPhase.PublicNetworkConfirmationRequired)
            return Diagnostic(FirewallDiagnosticReason.PublicProfileApprovalRequired,
                FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.ConfirmPublic,
                FirewallRecoveryAction.CheckAgain, retryable: true, uac: true);
        if (state.Phase == FirewallAccessPhase.NeedsPermission)
            return Diagnostic(FirewallDiagnosticReason.None, FirewallDiagnosticSeverity.Neutral,
                FirewallRecoveryAction.Configure, FirewallRecoveryAction.CheckAgain,
                retryable: true, uac: true);
        return Diagnostic(FirewallDiagnosticReason.FirewallStateUnknown,
            FirewallDiagnosticSeverity.Warning, FirewallRecoveryAction.CheckAgain, retryable: true);
    }

    private static FirewallDiagnostic Diagnostic(
        FirewallDiagnosticReason reason,
        FirewallDiagnosticSeverity severity,
        FirewallRecoveryAction primary = FirewallRecoveryAction.None,
        FirewallRecoveryAction secondary = FirewallRecoveryAction.None,
        bool retryable = false,
        bool uac = false) => new()
        {
            Reason = reason,
            Severity = severity,
            PrimaryAction = primary,
            SecondaryAction = secondary,
            AutomaticRecoveryAllowed = false,
            UacRequired = uac,
            Retryable = retryable
        };
}

/// <summary>
/// Provider-neutral rules for ChunkPilot-owned Windows Firewall access. Everything here is a pure
/// decision, so the rule that runs in the Agent and in the privileged helper is the rule a unit test
/// proves.
/// </summary>
/// <remarks>
/// <para>
/// Two Microsoft behaviours shape this file. First, <c>INetFwRules::Add</c> overwrites an existing rule
/// with the same identifier, so nothing may ever be added without first establishing that the
/// identifier is either free or provably ChunkPilot's. Second, Windows Firewall resolves an explicit
/// block rule ahead of any conflicting allow rule, so an allow rule ChunkPilot creates is never, on its
/// own, evidence that traffic will pass.
/// </para>
/// <para>
/// Nothing here mutates anything, and nothing here consults the machine.
/// </para>
/// </remarks>
public static class WindowsFirewallPolicy
{
    /// <summary>The group every ChunkPilot rule belongs to, so an administrator can find them all.</summary>
    public const string RuleGroup = "ChunkPilot";

    /// <summary>The firewall API's "any protocol" value.</summary>
    public const int ProtocolAny = 256;

    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;

    public const string AnyAddress = "*";
    public const string AllInterfaceTypes = "All";

    /// <summary>The remote scope Microsoft recommends for home-only services, and which this feature must not use.</summary>
    public const string LocalSubnetScope = "LocalSubnet";

    private const string RuleNamePrefix = "ChunkPilot Minecraft server (";

    public static int ProtocolFor(MappingTransport transport) =>
        transport == MappingTransport.Udp ? ProtocolUdp : ProtocolTcp;

    /// <summary>
    /// The rule's Windows name. Derived from the rule identifier alone, so it cannot collide casually
    /// with another application's rule and cannot change when the server is renamed.
    /// </summary>
    public static string RuleName(Guid ruleId) =>
        $"{RuleNamePrefix}{ruleId.ToString("D", CultureInfo.InvariantCulture)})";

    /// <summary>
    /// The rule's description: enough for an administrator reading Windows Defender Firewall to know
    /// what it is and how to withdraw it, plus the rule identifier that proves who created it.
    /// </summary>
    /// <remarks>
    /// Deliberately carries no server name, no user name and no machine name. The program path is
    /// already a property of the rule, so repeating it here would add exposure without adding meaning.
    /// </remarks>
    public static string RuleDescription(Guid ruleId, int port, MappingTransport transport) =>
        $"Inbound Minecraft access created by ChunkPilot for one server on " +
        $"{TransportName(transport)} {port.ToString(CultureInfo.InvariantCulture)}. " +
        $"ChunkPilot rule id {ruleId.ToString("D", CultureInfo.InvariantCulture)}. " +
        "Remove it from ChunkPilot, or delete this rule, to withdraw the access.";

    public static string TransportName(MappingTransport transport) =>
        transport == MappingTransport.Udp ? "UDP" : "TCP";

    /// <summary>
    /// Whether a rule carries ChunkPilot's own evidence for <paramref name="ruleId"/>.
    /// </summary>
    /// <remarks>
    /// Name alone is never enough — a name is something anyone can type. The group and the rule
    /// identifier written into the description have to agree with it as well, which is what lets the
    /// privileged helper refuse to overwrite a rule it did not create even without the database.
    /// </remarks>
    public static bool CarriesOwnershipEvidence(FirewallRuleSnapshot rule, Guid ruleId)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (ruleId == Guid.Empty)
            return false;
        return rule.Name.Equals(RuleName(ruleId), StringComparison.Ordinal) &&
               rule.Grouping.Equals(RuleGroup, StringComparison.Ordinal) &&
               rule.Description.Contains(ruleId.ToString("D", CultureInfo.InvariantCulture),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a rule Windows reports can be proven to be the one ChunkPilot recorded creating.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative, and in this order: persisted evidence that ChunkPilot created a rule
    /// with this identifier comes first, because without it the rule belongs to somebody else however
    /// much the rest matches.
    /// </remarks>
    public static bool ProvesOwnership(FirewallAccessRecord record, FirewallRuleSnapshot rule)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rule);
        if (!record.Configured && !record.RemovalPending)
            return false;
        if (record.RuleId == Guid.Empty)
            return false;
        if (!record.RuleName.Equals(RuleName(record.RuleId), StringComparison.Ordinal))
            return false;
        return CarriesOwnershipEvidence(rule, record.RuleId);
    }

    /// <summary>Finds the rule carrying this record's identifier, whoever ends up owning it.</summary>
    public static FirewallRuleSnapshot? FindByName(
        string ruleName, IReadOnlyList<FirewallRuleSnapshot> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrEmpty(ruleName))
            return null;
        foreach (var rule in rules)
        {
            if (rule.Name.Equals(ruleName, StringComparison.Ordinal))
                return rule;
        }
        return null;
    }

    /// <summary>
    /// Every way a rule differs from the plan it is supposed to be. Empty means exact.
    /// </summary>
    /// <remarks>
    /// This is the postcondition. A mutation that "succeeded" is only projected as configured once this
    /// returns nothing, because a rule object existing is not the same fact as a rule being right.
    /// </remarks>
    public static IReadOnlyList<string> Differences(FirewallRulePlan plan, FirewallRuleSnapshot rule)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rule);
        var differences = new List<string>();
        if (rule.UnavailableFields != FirewallRuleUnavailableFields.None)
            differences.Add($"the rule has unreadable properties ({rule.UnavailableFields})");
        if (!rule.Enabled)
            differences.Add("the rule is switched off");
        if (rule.Direction != FirewallRuleDirection.Inbound)
            differences.Add("the rule is not an inbound rule");
        if (rule.Action != FirewallRuleAction.Allow)
            differences.Add("the rule does not allow traffic");
        AddConditionDifferences(plan, rule, differences);
        return differences;
    }

    private static void AddConditionDifferences(
        FirewallRulePlan plan, FirewallRuleSnapshot rule, ICollection<string> differences)
    {
        if (rule.Protocol != plan.Protocol)
            differences.Add($"the protocol is not {TransportName(plan.Transport)}");
        if (!IsExactlyPort(rule.LocalPorts, plan.Port))
            differences.Add($"the local port is not {plan.Port.ToString(CultureInfo.InvariantCulture)}");
        if (!SamePath(rule.ApplicationName, plan.ProgramPath))
            differences.Add("the program is not this server's Java runtime");
        if (rule.AppliesToAllProfiles || rule.Profiles != plan.Profiles)
            differences.Add("the rule applies to different Windows network profiles");
        if (rule.EdgeTraversal)
            differences.Add("edge traversal is switched on");
        if (!IsAnyAddress(rule.RemoteAddresses))
            differences.Add("the remote address scope has been narrowed");
        if (!IsAnyAddress(rule.LocalAddresses))
            differences.Add("the local address scope has been narrowed");
        if (!IsAnyAddress(rule.RemotePorts))
            differences.Add("the remote port scope has been narrowed");
        if (!rule.InterfaceTypes.Equals(plan.InterfaceTypes, StringComparison.OrdinalIgnoreCase))
            differences.Add("the rule applies to different interface types");
        if (rule.Interfaces.Count > 0)
            differences.Add("the rule is limited to named network interfaces");
        if (rule.ServiceName.Length > 0)
            differences.Add("the rule is bound to a Windows service");
        if (rule.IcmpTypesAndCodes.Length > 0)
            differences.Add("the rule carries an ICMP condition");
        if (rule.EdgeTraversalOptions != 0)
            differences.Add("edge traversal options are enabled");
        if (rule.LocalAppPackageId.Length > 0)
            differences.Add("the rule is bound to an app package or AppContainer");
        if (rule.LocalUserOwner.Length > 0)
            differences.Add("the rule is bound to a local user owner");
        if (rule.LocalUserAuthorizedList.Length > 0)
            differences.Add("the rule has an authorized local-user condition");
        if (rule.RemoteUserAuthorizedList.Length > 0)
            differences.Add("the rule has an authorized remote-user condition");
        if (rule.RemoteMachineAuthorizedList.Length > 0)
            differences.Add("the rule has an authorized remote-machine condition");
        if (rule.SecureFlags != 0)
            differences.Add("the rule requires IPsec authentication or protection");
    }

    public static bool Matches(FirewallRulePlan plan, FirewallRuleSnapshot rule) =>
        Differences(plan, rule).Count == 0;

    /// <summary>
    /// A rule somebody else created that already permits exactly the traffic the plan needs.
    /// </summary>
    /// <remarks>
    /// Only exact equivalence suppresses setup. Broad or constrained foreign allows remain untouched
    /// and are retained only as technical evidence so ChunkPilot can establish deterministic ownership
    /// of its own narrow Java, TCP port, and profile rule.
    /// </remarks>
    public static FirewallRuleSnapshot? FindCoveringAllowRule(
        FirewallRulePlan plan, IReadOnlyList<FirewallRuleSnapshot> rules, Guid ownedRuleId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (rule.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Action) ||
                rule.Action != FirewallRuleAction.Allow)
                continue;
            if (ownedRuleId != Guid.Empty && CarriesOwnershipEvidence(rule, ownedRuleId))
                continue;
            if (EvaluateRuleCoverage(plan, rule).ProvesExactEquivalent)
                return rule;
        }
        return null;
    }

    /// <summary>
    /// An explicit block rule that applies to this traffic. Explicit blocks win over allow rules, so an
    /// allow rule beside one is not evidence that anything will connect.
    /// </summary>
    public static FirewallRuleSnapshot? FindApplicableBlockRule(
        FirewallRulePlan plan, IReadOnlyList<FirewallRuleSnapshot> rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (rule.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Action) ||
                rule.Action != FirewallRuleAction.Block)
                continue;
            var evaluation = EvaluateRuleCoverage(plan, rule);
            if (evaluation.MayApply && evaluation.Coverage != FirewallRuleCoverage.UnknownOrUnsupported)
                return rule;
        }
        return null;
    }

    /// <summary>Finds a potentially applicable block whose conditions cannot be proved either way.</summary>
    public static FirewallRuleSnapshot? FindPotentiallyApplicableUnknownBlockRule(
        FirewallRulePlan plan, IReadOnlyList<FirewallRuleSnapshot> rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            var actionUnknown = rule.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Action);
            if (!actionUnknown && rule.Action != FirewallRuleAction.Block)
                continue;
            if (EvaluateRuleCoverage(plan, rule).Coverage == FirewallRuleCoverage.UnknownOrUnsupported)
                return rule;
        }
        return null;
    }

    /// <summary>Finds non-equivalent foreign allow evidence without treating it as readiness.</summary>
    public static FirewallRuleEvaluation? FindOtherForeignAllowRule(
        FirewallRulePlan plan, IReadOnlyList<FirewallRuleSnapshot> rules, Guid ownedRuleId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (rule.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Action) ||
                rule.Action != FirewallRuleAction.Allow)
                continue;
            if (ownedRuleId != Guid.Empty && CarriesOwnershipEvidence(rule, ownedRuleId))
                continue;
            var evaluation = EvaluateRuleCoverage(plan, rule);
            if (evaluation.Coverage is FirewallRuleCoverage.BroadUnrestricted or
                FirewallRuleCoverage.ConstrainedMatch or FirewallRuleCoverage.UnknownOrUnsupported)
                return evaluation;
        }
        return null;
    }

    /// <summary>Conservatively evaluates every documented match dimension represented by the model.</summary>
    public static FirewallRuleEvaluation EvaluateRuleCoverage(
        FirewallRulePlan plan, FirewallRuleSnapshot rule)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rule);

        bool Known(FirewallRuleUnavailableFields field) => !rule.UnavailableFields.HasFlag(field);

        if (Known(FirewallRuleUnavailableFields.Enabled) && !rule.Enabled ||
            Known(FirewallRuleUnavailableFields.Direction) && rule.Direction != FirewallRuleDirection.Inbound ||
            Known(FirewallRuleUnavailableFields.Profiles) && !rule.AppliesToAllProfiles &&
                (rule.Profiles & plan.Profiles) == 0 ||
            Known(FirewallRuleUnavailableFields.Protocol) && rule.Protocol != ProtocolAny &&
                rule.Protocol != plan.Protocol ||
            Known(FirewallRuleUnavailableFields.LocalPorts) && !CoversPort(rule.LocalPorts, plan.Port))
            return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.DoesNotMatch);

        if (Known(FirewallRuleUnavailableFields.ApplicationName) && rule.ApplicationName.Length > 0 &&
                !SamePath(rule.ApplicationName, plan.ProgramPath) ||
            Known(FirewallRuleUnavailableFields.ServiceName) && rule.ServiceName.Length > 0 ||
            Known(FirewallRuleUnavailableFields.LocalAppPackageId) && rule.LocalAppPackageId.Length > 0)
            return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.ConstrainedDoesNotMatch);

        var constrained = false;
        if (Known(FirewallRuleUnavailableFields.LocalAddresses) && !IsAnyAddress(rule.LocalAddresses))
        {
            var addressMatch = AddressScopeContains(rule.LocalAddresses, plan.TargetLocalAddress);
            if (addressMatch == ConditionMatch.Excludes)
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.ConstrainedDoesNotMatch);
            if (addressMatch == ConditionMatch.Unknown)
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.UnknownOrUnsupported);
            constrained = true;
        }

        if (Known(FirewallRuleUnavailableFields.Interfaces) && rule.Interfaces.Count > 0)
        {
            if (plan.TargetInterfaceName.Length == 0)
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.UnknownOrUnsupported);
            if (!rule.Interfaces.Contains(plan.TargetInterfaceName, StringComparer.OrdinalIgnoreCase))
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.ConstrainedDoesNotMatch);
            constrained = true;
        }

        if (Known(FirewallRuleUnavailableFields.InterfaceTypes) && !IsAllInterfaceTypes(rule.InterfaceTypes))
        {
            var interfaceMatch = InterfaceTypesContain(rule.InterfaceTypes, plan.TargetInterfaceType);
            if (interfaceMatch == ConditionMatch.Excludes)
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.ConstrainedDoesNotMatch);
            if (interfaceMatch == ConditionMatch.Unknown)
                return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.UnknownOrUnsupported);
            constrained = true;
        }

        if ((rule.UnavailableFields & CoverageRelevantUnavailableFields) != FirewallRuleUnavailableFields.None)
            return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.UnknownOrUnsupported);

        if (rule.LocalUserOwner.Length > 0 || rule.LocalUserAuthorizedList.Length > 0 ||
            rule.RemoteUserAuthorizedList.Length > 0 || rule.RemoteMachineAuthorizedList.Length > 0 ||
            rule.SecureFlags != 0 || rule.IcmpTypesAndCodes.Length > 0)
            return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.UnknownOrUnsupported);

        if (!IsAnyAddress(rule.RemoteAddresses) || !IsAnyAddress(rule.RemotePorts))
            constrained = true;

        var conditionDifferences = new List<string>();
        AddConditionDifferences(plan, rule, conditionDifferences);
        if (conditionDifferences.Count == 0)
            return new FirewallRuleEvaluation(rule, FirewallRuleCoverage.ExactEquivalent);
        return new FirewallRuleEvaluation(rule, constrained
            ? FirewallRuleCoverage.ConstrainedMatch
            : FirewallRuleCoverage.BroadUnrestricted);
    }

    private const FirewallRuleUnavailableFields CoverageRelevantUnavailableFields =
        FirewallRuleUnavailableFields.Enabled | FirewallRuleUnavailableFields.Direction |
        FirewallRuleUnavailableFields.Action | FirewallRuleUnavailableFields.Protocol |
        FirewallRuleUnavailableFields.LocalPorts | FirewallRuleUnavailableFields.RemotePorts |
        FirewallRuleUnavailableFields.IcmpTypesAndCodes | FirewallRuleUnavailableFields.LocalAddresses |
        FirewallRuleUnavailableFields.RemoteAddresses | FirewallRuleUnavailableFields.ApplicationName |
        FirewallRuleUnavailableFields.ServiceName | FirewallRuleUnavailableFields.Profiles |
        FirewallRuleUnavailableFields.Interfaces | FirewallRuleUnavailableFields.InterfaceTypes |
        FirewallRuleUnavailableFields.EdgeTraversal | FirewallRuleUnavailableFields.EdgeTraversalOptions |
        FirewallRuleUnavailableFields.LocalAppPackageId | FirewallRuleUnavailableFields.LocalUserOwner |
        FirewallRuleUnavailableFields.LocalUserAuthorizedList |
        FirewallRuleUnavailableFields.RemoteUserAuthorizedList |
        FirewallRuleUnavailableFields.RemoteMachineAuthorizedList | FirewallRuleUnavailableFields.SecureFlags;

    private enum ConditionMatch { Includes, Excludes, Unknown }

    private static bool IsAllInterfaceTypes(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals(AllInterfaceTypes, StringComparison.OrdinalIgnoreCase);

    private static ConditionMatch InterfaceTypesContain(string value, string targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
            return ConditionMatch.Unknown;
        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
            return ConditionMatch.Includes;
        if (values.Any(item => item.Equals(targetType, StringComparison.OrdinalIgnoreCase)))
            return ConditionMatch.Includes;
        return values.All(item => item.Equals("Lan", StringComparison.OrdinalIgnoreCase) ||
                                  item.Equals("Wireless", StringComparison.OrdinalIgnoreCase) ||
                                  item.Equals("RemoteAccess", StringComparison.OrdinalIgnoreCase))
            ? ConditionMatch.Excludes
            : ConditionMatch.Unknown;
    }

    private static ConditionMatch AddressScopeContains(string scope, string targetAddress)
    {
        if (IsAnyAddress(scope) || scope.Trim().Equals(LocalSubnetScope, StringComparison.OrdinalIgnoreCase))
            return ConditionMatch.Includes;
        if (!IPAddress.TryParse(targetAddress, out var target))
            return ConditionMatch.Unknown;

        var sawUnknown = false;
        foreach (var token in scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(token, out var single))
            {
                if (single.Equals(target))
                    return ConditionMatch.Includes;
                continue;
            }

            var range = token.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 && IPAddress.TryParse(range[0], out var low) &&
                IPAddress.TryParse(range[1], out var high) && AddressInRange(target, low, high))
                return ConditionMatch.Includes;

            var prefix = token.Split('/', 2, StringSplitOptions.TrimEntries);
            if (prefix.Length == 2 && IPAddress.TryParse(prefix[0], out var network) &&
                int.TryParse(prefix[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            {
                if (AddressInPrefix(target, network, bits))
                    return ConditionMatch.Includes;
                continue;
            }
            sawUnknown = true;
        }
        return sawUnknown ? ConditionMatch.Unknown : ConditionMatch.Excludes;
    }

    private static bool AddressInRange(IPAddress target, IPAddress low, IPAddress high)
    {
        var targetBytes = target.GetAddressBytes();
        var lowBytes = low.GetAddressBytes();
        var highBytes = high.GetAddressBytes();
        if (targetBytes.Length != lowBytes.Length || targetBytes.Length != highBytes.Length)
            return false;
        return CompareAddress(targetBytes, lowBytes) >= 0 && CompareAddress(targetBytes, highBytes) <= 0;
    }

    private static int CompareAddress(byte[] left, byte[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool AddressInPrefix(IPAddress target, IPAddress network, int bits)
    {
        var targetBytes = target.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (targetBytes.Length != networkBytes.Length || bits < 0 || bits > targetBytes.Length * 8)
            return false;
        for (var index = 0; index < targetBytes.Length; index++)
        {
            var remaining = bits - index * 8;
            if (remaining <= 0)
                return true;
            var mask = remaining >= 8 ? 0xff : 0xff << (8 - remaining) & 0xff;
            if ((targetBytes[index] & mask) != (networkBytes[index] & mask))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Which single firewall profile a Windows network category maps to.
    /// </summary>
    /// <remarks>
    /// The category is only ever used to choose which profile a rule belongs to. It is never used to
    /// decide whether traffic is already allowed: Microsoft states plainly that the private and public
    /// categories must not be used to assume which ports are open, and that the firewall APIs must be
    /// called instead. This file does exactly that everywhere else.
    /// </remarks>
    public static FirewallProfile ProfileFor(WindowsNetworkCategory category) => category switch
    {
        WindowsNetworkCategory.Private => FirewallProfile.Private,
        WindowsNetworkCategory.Public => FirewallProfile.Public,
        WindowsNetworkCategory.DomainAuthenticated => FirewallProfile.Domain,
        _ => FirewallProfile.None
    };

    /// <summary>
    /// Whether a path may be used as the program of a ChunkPilot firewall rule.
    /// </summary>
    /// <remarks>
    /// The rule has to name the process that actually owns the Minecraft listener. A launcher — a batch
    /// file, <c>cmd.exe</c>, PowerShell — is not that process, and a rule naming one would either do
    /// nothing or, worse, permit inbound traffic to a shell. Anything that is not an existing, fully
    /// qualified Java launcher is refused outright; the caller must then create no program rule at all
    /// rather than fall back to something broader.
    /// </remarks>
    public static bool IsTrustworthyJavaRuntime(string? path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "No Java runtime is recorded for this server.";
            return false;
        }
        var trimmed = path.Trim();
        if (trimmed.Contains('|', StringComparison.Ordinal) ||
            trimmed.Contains('*', StringComparison.Ordinal) ||
            trimmed.Contains('?', StringComparison.Ordinal) ||
            trimmed.Contains('"', StringComparison.Ordinal) ||
            trimmed.Any(char.IsControl))
        {
            reason = "The recorded runtime path contains characters a firewall rule cannot carry.";
            return false;
        }
        if (!Path.IsPathFullyQualified(trimmed))
        {
            reason = "The recorded runtime path is not a full path.";
            return false;
        }
        var fileName = Path.GetFileName(trimmed);
        if (!fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{fileName} is not a Java runtime, so ChunkPilot will not create a rule for it.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Whether a rule's local-port string is exactly one port and that port is this one.</summary>
    public static bool IsExactlyPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
            return false;
        var trimmed = localPorts.Trim();
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
               parsed == port;
    }

    /// <summary>
    /// Whether a rule's local-port string admits this port. Understands a single port, a comma list, a
    /// hyphenated range and the wildcard; anything it cannot read (for example "RPC") is treated as not
    /// covering, because guessing wider would be the unsafe direction.
    /// </summary>
    public static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
            return true;
        var trimmed = localPorts.Trim();
        if (trimmed == AnyAddress)
            return true;
        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (range.Length == 1 &&
                int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var single) &&
                single == port)
                return true;
            if (range.Length == 2 &&
                int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low) &&
                int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var high) &&
                port >= low && port <= high)
                return true;
        }
        return false;
    }

    /// <summary>Whether a remote-address scope admits any address, which internet clients require.</summary>
    public static bool IsAnyAddress(string? addresses) =>
        string.IsNullOrWhiteSpace(addresses) || addresses.Trim() == AnyAddress;

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return Path.GetFullPath(left.Trim()).Equals(Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Human-readable profile list for Technical details.</summary>
    public static string DescribeProfiles(FirewallProfile profiles)
    {
        if (profiles == FirewallProfile.None)
            return "None";
        var names = new List<string>(3);
        if (profiles.HasFlag(FirewallProfile.Domain))
            names.Add("Domain");
        if (profiles.HasFlag(FirewallProfile.Private))
            names.Add("Private");
        if (profiles.HasFlag(FirewallProfile.Public))
            names.Add("Public");
        return string.Join(", ", names);
    }
}
