using System.Globalization;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChunkPilot.Core;

namespace ChunkPilot.FirewallHelper;

/// <summary>
/// The only privileged code in ChunkPilot: adding and removing one Windows Firewall rule through the
/// documented Windows Firewall with Advanced Security COM API.
/// </summary>
/// <remarks>
/// <para>
/// It creates <c>HNetCfg.FwPolicy2</c> and <c>HNetCfg.FWRule</c>, sets the exact properties of one
/// inbound allow rule, and calls <c>INetFwRules::Add</c> or <c>INetFwRules::Remove</c>. It never
/// touches <c>FirewallEnabled</c>, <c>DefaultInboundAction</c>, <c>DefaultOutboundAction</c>,
/// <c>NotificationsDisabled</c>, <c>BlockAllInboundTraffic</c>, <c>ExcludedInterfaces</c>,
/// <c>EnableRuleGroup</c> or <c>RestoreLocalFirewallDefaults</c>, and it never contacts the registry,
/// the service control manager, Group Policy or the Network List Manager.
/// </para>
/// <para>
/// Rule properties are assigned before the rule is added, because Windows Firewall commits and
/// validates a rule on every property change once it is in the collection.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class NetFwMutationBackend : IFirewallMutationBackend, IDisposable
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FWRule";
    private const int AllProfilesValue = 0x7fffffff;

    private readonly object? policy;

    public NetFwMutationBackend()
    {
        var type = Type.GetTypeFromProgID(PolicyProgId);
        policy = type is null ? null : Activator.CreateInstance(type);
        Status = ResolveStatus();
    }

    public FirewallBackendStatus Status { get; }

    public IReadOnlyList<FirewallRuleSnapshot> ReadRules()
    {
        if (policy is null)
            return [];
        object? collection = null;
        try
        {
            collection = policy.GetType().InvokeMember(
                "Rules", BindingFlags.GetProperty, null, policy, null);
            if (collection is null)
                throw new InvalidOperationException("The Windows Firewall rule collection is unavailable.");
            var results = new List<FirewallRuleSnapshot>(512);
            foreach (var item in Enumerate(collection))
            {
                try
                {
                    results.Add(Read(item));
                }
                catch (Exception exception) when (exception is COMException or MissingMemberException or
                                                      InvalidCastException or TargetInvocationException)
                {
                    throw new InvalidOperationException(
                        "A Windows Firewall rule changed while ownership was being verified; nothing was changed.",
                        exception);
                }
                finally
                {
                    Release(item);
                }
            }
            return results;
        }
        finally
        {
            Release(collection);
        }
    }

    public void AddRule(FirewallRulePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (policy is null)
            throw new InvalidOperationException("The Windows Firewall policy component is unavailable.");
        var ruleType = Type.GetTypeFromProgID(RuleProgId)
                       ?? throw new InvalidOperationException(
                           $"The firewall rule component {RuleProgId} is not registered.");
        object? rules = null;
        object? rule = null;
        try
        {
            rule = Activator.CreateInstance(ruleType)
                   ?? throw new InvalidOperationException("A firewall rule object could not be created.");
            dynamic candidate = rule;
            candidate.Name = plan.RuleName;
            candidate.Description = plan.Description;
            candidate.Grouping = plan.Grouping;
            candidate.ApplicationName = plan.ProgramPath;
            candidate.Protocol = plan.Protocol;
            candidate.LocalPorts = plan.Port.ToString(CultureInfo.InvariantCulture);
            candidate.LocalAddresses = plan.LocalAddresses;
            candidate.RemoteAddresses = plan.RemoteAddresses;
            candidate.Direction = (int)plan.Direction;
            candidate.Action = (int)plan.Action;
            candidate.Profiles = (int)plan.Profiles;
            candidate.InterfaceTypes = plan.InterfaceTypes;
            candidate.EdgeTraversal = plan.EdgeTraversal;
            candidate.Enabled = true;

            rules = policy.GetType().InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null)
                    ?? throw new InvalidOperationException("The firewall rule collection is unavailable.");
            _ = rules.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, rules, [rule]);
        }
        finally
        {
            Release(rule);
            Release(rules);
        }
    }

    public void RemoveRule(string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        if (policy is null)
            throw new InvalidOperationException("The Windows Firewall policy component is unavailable.");
        object? rules = null;
        try
        {
            rules = policy.GetType().InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null)
                    ?? throw new InvalidOperationException("The firewall rule collection is unavailable.");
            _ = rules.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, rules, [ruleName]);
        }
        finally
        {
            Release(rules);
        }
    }

    private FirewallBackendStatus ResolveStatus()
    {
        if (policy is null)
            return FirewallBackendStatus.Unavailable;
        try
        {
            var modifyState = Convert.ToInt32(policy.GetType().InvokeMember(
                "LocalPolicyModifyState", BindingFlags.GetProperty, null, policy, null),
                CultureInfo.InvariantCulture);
            return (FirewallPolicyModifyState)modifyState switch
            {
                FirewallPolicyModifyState.GroupPolicyOverride => FirewallBackendStatus.GroupPolicyOverride,
                FirewallPolicyModifyState.InboundBlocked => FirewallBackendStatus.InboundBlocked,
                FirewallPolicyModifyState.Ok => FirewallBackendStatus.Available,
                _ => FirewallBackendStatus.Unavailable
            };
        }
        catch (Exception exception) when (exception is COMException or MissingMemberException or
                                              InvalidCastException or TargetInvocationException)
        {
            return FirewallBackendStatus.Unavailable;
        }
    }

    private static FirewallRuleSnapshot Read(object item)
    {
        dynamic rule = item;
        int rawProfiles = rule.Profiles;
        return new FirewallRuleSnapshot
        {
            Name = Text(rule.Name),
            Description = Text(rule.Description),
            Grouping = Text(rule.Grouping),
            Enabled = rule.Enabled,
            Direction = (FirewallRuleDirection)(int)rule.Direction,
            Action = (int)rule.Action == 1 ? FirewallRuleAction.Allow : FirewallRuleAction.Block,
            Protocol = rule.Protocol,
            LocalPorts = Text(rule.LocalPorts),
            RemotePorts = Text(rule.RemotePorts),
            LocalAddresses = Text(rule.LocalAddresses),
            RemoteAddresses = Text(rule.RemoteAddresses),
            ApplicationName = Text(rule.ApplicationName),
            ServiceName = Text(rule.ServiceName),
            Profiles = (FirewallProfile)(rawProfiles & (int)(FirewallProfile.Domain |
                                                             FirewallProfile.Private | FirewallProfile.Public)),
            AppliesToAllProfiles = (rawProfiles & AllProfilesValue) == AllProfilesValue,
            EdgeTraversal = rule.EdgeTraversal,
            InterfaceTypes = Text(rule.InterfaceTypes)
        };
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        IEnumerator? enumerator;
        try
        {
            enumerator = collection.GetType().InvokeMember(
                "_NewEnum", BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                null, collection, null) as IEnumerator;
        }
        catch (Exception exception) when (exception is COMException or MissingMemberException or
                                              TargetInvocationException)
        {
            throw new InvalidOperationException(
                "Windows Firewall rules could not be enumerated; nothing was changed.", exception);
        }
        return enumerator is null
            ? throw new InvalidOperationException(
                "Windows Firewall did not return a rule enumerator; nothing was changed.")
            : Walk(enumerator);
    }

    private static IEnumerable<object> Walk(IEnumerator enumerator)
    {
        try
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is { } value)
                    yield return value;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static string Text(object? value) => value as string ?? "";

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            _ = Marshal.ReleaseComObject(comObject);
    }

    public void Dispose() => Release(policy);
}
