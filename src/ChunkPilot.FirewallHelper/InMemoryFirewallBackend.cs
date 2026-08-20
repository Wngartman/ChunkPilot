using System.Globalization;
using ChunkPilot.Core;

namespace ChunkPilot.FirewallHelper;

/// <summary>
/// A firewall store that exists only inside this process.
/// </summary>
/// <remarks>
/// Used by <c>--self-test</c> so the shipped executable can be proven to parse, validate and act
/// correctly without the real Windows policy store ever being opened. It holds no privilege of any
/// kind: it is a list.
/// </remarks>
internal sealed class InMemoryFirewallBackend : IFirewallMutationBackend
{
    private readonly List<FirewallRuleSnapshot> rules = [];

    public FirewallBackendStatus Status => FirewallBackendStatus.Available;

    public IReadOnlyList<FirewallRuleSnapshot> ReadRules() => rules.ToArray();

    public void AddRule(FirewallRulePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Mirrors the documented behaviour of INetFwRules::Add: a rule with the same identifier is
        // replaced. Every caller has already established that this identifier is free or its own.
        rules.RemoveAll(rule => rule.Name.Equals(plan.RuleName, StringComparison.Ordinal));
        rules.Add(new FirewallRuleSnapshot
        {
            Name = plan.RuleName,
            Description = plan.Description,
            Grouping = plan.Grouping,
            Enabled = true,
            Direction = plan.Direction,
            Action = plan.Action,
            Protocol = plan.Protocol,
            LocalPorts = plan.Port.ToString(CultureInfo.InvariantCulture),
            LocalAddresses = plan.LocalAddresses,
            RemoteAddresses = plan.RemoteAddresses,
            ApplicationName = plan.ProgramPath,
            Profiles = plan.Profiles,
            EdgeTraversal = plan.EdgeTraversal,
            InterfaceTypes = plan.InterfaceTypes
        });
    }

    public void RemoveRule(string ruleName) =>
        rules.RemoveAll(rule => rule.Name.Equals(ruleName, StringComparison.Ordinal));
}
