using System.Globalization;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.Presentation;

/// <summary>
/// Turns the Agent's authoritative external reachability state into beginner-facing wording.
/// </summary>
/// <remarks>
/// <para>
/// The rule every line here obeys: a completed TCP handshake proves that the public network path
/// reached the server's listening port, and proves nothing else. It does not prove that a friend's
/// Minecraft version matches, that they will pass the whitelist, that they are not banned, that mods
/// line up, or that the game will play well. Nothing in this file says any of those things.
/// </para>
/// <para>
/// A failed handshake is treated with the same discipline in reverse: it means the probe could not
/// establish TCP, and nothing about why. A cause is only ever named when other evidence ChunkPilot
/// already holds supports it, and then only as a possibility.
/// </para>
/// </remarks>
public static class ExternalReachabilityPresentation
{
    public static string Title(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            ExternalReachabilityPhase.NotChecked => "Not checked",
            ExternalReachabilityPhase.Ineligible => IneligibleTitle(state.Blocker),
            ExternalReachabilityPhase.Checking => "Checking from outside…",
            ExternalReachabilityPhase.Reachable => "Public access verified",
            ExternalReachabilityPhase.Unreachable => "Could not reach this server from outside",
            ExternalReachabilityPhase.SourceAddressMismatch => "Different public address detected",
            ExternalReachabilityPhase.UnsupportedAddressFamily => "The outside check used a different kind of address",
            ExternalReachabilityPhase.ProbeUnavailable => "External check is temporarily unavailable",
            ExternalReachabilityPhase.RateLimited => "External checks are temporarily limited",
            ExternalReachabilityPhase.Cancelled => "External check cancelled",
            ExternalReachabilityPhase.Stale => "Not verified for the current setup",
            _ => "Not checked"
        };
    }

    /// <summary>
    /// The primary explanation. Composed from the states ChunkPilot already owns — it never rewrites
    /// them, and it never invents a cause the evidence does not carry.
    /// </summary>
    public static string Summary(
        ExternalReachabilityState state, RouterMappingState router, WindowsFirewallState firewall)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(firewall);
        return state.Phase switch
        {
            ExternalReachabilityPhase.NotChecked =>
                "ChunkPilot has not checked this server from outside your home network. " +
                "Connecting from this computer cannot prove it, so a check is the only way to know.",
            ExternalReachabilityPhase.Ineligible => IneligibleSummary(state.Blocker),
            ExternalReachabilityPhase.Checking =>
                "Asking the outside check to connect to this server's public port. Nothing on your computer, " +
                "your router or your firewall is being changed.",
            ExternalReachabilityPhase.Reachable => ReachableSummary(state),
            ExternalReachabilityPhase.Unreachable => UnreachableSummary(router, firewall),
            ExternalReachabilityPhase.SourceAddressMismatch =>
                "The external check reached the internet through a different address than your router reports. " +
                "A VPN, another network path, or upstream NAT may be involved. Nothing was changed.",
            ExternalReachabilityPhase.UnsupportedAddressFamily =>
                "The check involved an address that can't be compared with the IPv4 port your router " +
                "forwards, so nothing was concluded about this server.",
            ExternalReachabilityPhase.ProbeUnavailable => state.ProbeConfigured
                ? "The outside check didn't complete. Your server settings were not changed."
                : "This build has no outside check available. Everything else about this server still works.",
            ExternalReachabilityPhase.RateLimited =>
                "Wait a moment, then try again. Your server settings were not changed.",
            ExternalReachabilityPhase.Cancelled =>
                "You stopped the check before it finished, so nothing was concluded. Your server settings were " +
                "not changed.",
            ExternalReachabilityPhase.Stale => StaleSummary(state),
            _ => "ChunkPilot has not checked this server from outside your home network."
        };
    }

    /// <summary>
    /// The one sentence that is allowed to state what was proven, and it stops exactly there.
    /// </summary>
    private static string ReachableSummary(ExternalReachabilityState state) =>
        $"TCP {Port(state)} answered from outside your network. That proves the connection reaches this " +
        "server's port; it doesn't check Minecraft versions, whitelists or mods.";

    /// <summary>
    /// Composed diagnosis. Each branch says only what the other layers actually establish, and the
    /// possible causes are offered as possibilities rather than asserted.
    /// </summary>
    private static string UnreachableSummary(RouterMappingState router, WindowsFirewallState firewall)
    {
        var routerActive = router.Phase == RouterMappingPhase.Active;
        var firewallSettled = firewall.Phase is FirewallAccessPhase.Configured or
            FirewallAccessPhase.ExistingWindowsRule or FirewallAccessPhase.FirewallDisabled;
        if (routerActive && firewallSettled)
            return "The router and Windows Firewall look configured, but the outside check still couldn't reach " +
                   "the server. An upstream router, your provider's network, or other security software may be " +
                   "in the way.";
        if (routerActive)
            return "Your router is forwarding the port, but Windows Firewall access isn't confirmed for this " +
                   "server, so that is the first thing worth settling.";
        return "The outside check couldn't establish a connection to this server's public port. " +
               "ChunkPilot won't guess why.";
    }

    private static string StaleSummary(ExternalReachabilityState state) => state.Blocker switch
    {
        ExternalReachabilityBlocker.ServerNotRunning =>
            "This server stopped after it was checked, so the earlier result no longer describes it. " +
            "Start the server and check again.",
        ExternalReachabilityBlocker.RouterMappingInactive or ExternalReachabilityBlocker.ExternalPortUnknown =>
            "The router port is no longer open, so the earlier result no longer describes this server.",
        ExternalReachabilityBlocker.DirectInternetOff =>
            "Direct internet was turned off after this server was checked.",
        ExternalReachabilityBlocker.PublicAddressUnknown or ExternalReachabilityBlocker.PublicAddressNotRoutable =>
            "Your public address changed after this server was checked, so the earlier result no longer " +
            "describes it.",
        _ => "Something changed after this server was checked — the port, the router entry, or the running " +
             "server itself — so the earlier result no longer describes it. Check again when you're ready."
    };

    private static string IneligibleTitle(ExternalReachabilityBlocker blocker) => blocker switch
    {
        ExternalReachabilityBlocker.ProbeNotConfigured => "Outside check not available in this build",
        ExternalReachabilityBlocker.ServerNotRunning => "Start the server first",
        ExternalReachabilityBlocker.LocalPortUnknown => "This server's port isn't known yet",
        ExternalReachabilityBlocker.DirectInternetOff => "Set up Direct internet first",
        ExternalReachabilityBlocker.RouterMappingInactive => "The router port isn't open right now",
        ExternalReachabilityBlocker.ExternalPortUnknown => "The router port isn't open right now",
        ExternalReachabilityBlocker.PublicAddressUnknown => "Your router hasn't reported an internet address",
        ExternalReachabilityBlocker.PublicAddressNotRoutable => "Your router does not have a public address",
        _ => "Not checked"
    };

    private static string IneligibleSummary(ExternalReachabilityBlocker blocker) => blocker switch
    {
        ExternalReachabilityBlocker.ProbeNotConfigured =>
            "This build has no outside check available. Everything else about this server still works, and " +
            "nothing is sent anywhere.",
        ExternalReachabilityBlocker.ServerNotRunning =>
            "There is nothing for an outside connection to reach until this server is running.",
        ExternalReachabilityBlocker.LocalPortUnknown =>
            "ChunkPilot needs this server's exact port before it can ask anything to connect to it.",
        ExternalReachabilityBlocker.DirectInternetOff =>
            "An outside connection needs a public port. Set up Direct internet above first.",
        ExternalReachabilityBlocker.RouterMappingInactive or ExternalReachabilityBlocker.ExternalPortUnknown =>
            "The router port opens while this server runs. There is nothing to check from outside until it does.",
        ExternalReachabilityBlocker.PublicAddressUnknown =>
            "ChunkPilot needs the address your router says it has on the internet before it can check it.",
        ExternalReachabilityBlocker.PublicAddressNotRoutable =>
            "Your router's own internet address is not one the wider internet can reach, so opening a port on " +
            "it cannot make this server reachable. Your provider may place another network layer above you.",
        _ => "ChunkPilot has not checked this server from outside your home network."
    };

    /// <summary>
    /// Tone never overstates, and never alarms. A verified external connection is the one state that
    /// has genuinely earned Success; a service that did not answer is not the user's problem and is
    /// deliberately neutral rather than a warning.
    /// </summary>
    public static AppTone Tone(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            ExternalReachabilityPhase.Reachable => AppTone.Success,
            ExternalReachabilityPhase.Checking => AppTone.Accent,
            ExternalReachabilityPhase.Unreachable or ExternalReachabilityPhase.SourceAddressMismatch or
                ExternalReachabilityPhase.UnsupportedAddressFamily => AppTone.Warning,
            ExternalReachabilityPhase.Ineligible when
                state.Blocker == ExternalReachabilityBlocker.PublicAddressNotRoutable => AppTone.Warning,
            _ => AppTone.Neutral
        };
    }

    /// <summary>A short non-colour status word, so the state is readable without seeing the tone.</summary>
    public static string Badge(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            ExternalReachabilityPhase.NotChecked => "Not checked",
            ExternalReachabilityPhase.Ineligible => "Not checked",
            ExternalReachabilityPhase.Checking => "Checking",
            ExternalReachabilityPhase.Reachable => "Verified",
            ExternalReachabilityPhase.Unreachable => "No answer",
            ExternalReachabilityPhase.SourceAddressMismatch => "Address differs",
            ExternalReachabilityPhase.UnsupportedAddressFamily => "Address type",
            ExternalReachabilityPhase.ProbeUnavailable => "Unavailable",
            ExternalReachabilityPhase.RateLimited => "Limited",
            ExternalReachabilityPhase.Cancelled => "Cancelled",
            ExternalReachabilityPhase.Stale => "Not current",
            _ => "Not checked"
        };
    }

    public static AppIconKind Icon(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            ExternalReachabilityPhase.Reachable => AppIconKind.Success,
            ExternalReachabilityPhase.Unreachable or ExternalReachabilityPhase.SourceAddressMismatch or
                ExternalReachabilityPhase.UnsupportedAddressFamily => AppIconKind.Warning,
            _ => AppIconKind.Globe
        };
    }

    /// <summary>The label for the one action, which changes with what the state allows next.</summary>
    public static string ActionText(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            ExternalReachabilityPhase.NotChecked or ExternalReachabilityPhase.Ineligible or
                ExternalReachabilityPhase.Stale => "Check from outside",
            _ => "Check again"
        };
    }

    /// <summary>
    /// The compact first-use explanation, shown beside the button before the first deliberate probe.
    /// Three facts and no modal: what is sent, what is not, and who sees it.
    /// </summary>
    public static string FirstUseNotice(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return $"ChunkPilot will ask its external probe to connect to this server's public TCP port. " +
               $"The check sends the port number ({Port(state)}) and uses the public address the probe sees. " +
               "No world, player, or server files are sent.";
    }

    /// <summary>Point-in-time evidence is labelled with its time, never presented as a standing fact.</summary>
    public static string VerifiedAt(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.CheckedAt is { } checkedAt
            ? $"Verified {checkedAt.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture)}"
            : "";
    }

    public static string CheckedAtLabel(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.CheckedAt is { } checkedAt
            ? checkedAt.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture)
            : "Never";
    }

    /// <summary>
    /// What the router says versus what the outside world saw. Shown for a mismatch, where the whole
    /// point is that the two disagree.
    /// </summary>
    public static string AddressComparison(ExternalReachabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var router = state.RouterReportedAddress.Length > 0 ? state.RouterReportedAddress : "not reported";
        var observed = state.ObservedAddress.Length > 0 ? state.ObservedAddress : "not reported";
        return $"Router-reported {router}; externally observed {observed}";
    }

    /// <summary>
    /// The CGNAT and double-NAT vocabulary, chosen by how strong the evidence actually is. A timeout
    /// never reaches this method: only an address that proves something does.
    /// </summary>
    public static string UpstreamAssessment(ExternalReachabilityState state, RouterMappingState router)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(router);
        var assessment = RouterMappingPolicy.ClassifyExternalAddress(router.RouterReportedExternalAddress);
        return assessment.Class switch
        {
            RoutableAddressClass.SharedAddressSpace =>
                "Router does not have a public address — its address is in the range providers use for " +
                "carrier-grade NAT.",
            RoutableAddressClass.PrivateUse =>
                "Router does not have a public address — another network layer appears to sit above it.",
            _ when state.Phase == ExternalReachabilityPhase.SourceAddressMismatch =>
                "Possible upstream NAT, VPN or other network path — the address your router reports is not the " +
                "one your traffic arrives from.",
            _ => ""
        };
    }

    private static string Port(ExternalReachabilityState state) => state.Port > 0
        ? state.Port.ToString(CultureInfo.InvariantCulture)
        : state.Endpoint.InternalPort > 0
            ? state.Endpoint.InternalPort.ToString(CultureInfo.InvariantCulture)
            : "the server's port";
}
