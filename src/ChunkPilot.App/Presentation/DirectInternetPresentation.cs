using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.Presentation;

/// <summary>
/// Turns the Agent's authoritative router-mapping state into beginner-facing wording.
/// </summary>
/// <remarks>
/// <para>
/// The one rule every line here obeys: a router accepting a mapping is not proof that a friend can
/// connect. Nothing in this file says reachable, connectable, open to the internet, or working. The
/// success wording is about the router only, and the tone for that state is deliberately not Success,
/// because ChunkPilot has confirmed a router action and not an end-to-end connection.
/// </para>
/// <para>
/// Protocol words — UPnP, PCP, NAT-PMP, SOAP, lease, gateway — appear only under Technical details.
/// </para>
/// </remarks>
public static class DirectInternetPresentation
{
    public static string Title(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Title(state.Phase);
    }

    public static string Title(RouterMappingPhase phase) => phase switch
    {
        RouterMappingPhase.Off => "Not set up",
        RouterMappingPhase.Checking => "Checking your router",
        RouterMappingPhase.Supported => "Your router can be set up automatically",
        RouterMappingPhase.Inactive => "Direct internet is set up",
        RouterMappingPhase.Unavailable => "Automatic setup unavailable",
        RouterMappingPhase.Undetermined => "Router did not answer",
        RouterMappingPhase.Creating => "Setting up your router",
        RouterMappingPhase.Active => "Router port is open",
        RouterMappingPhase.Conflict => "That port is already in use on your router",
        RouterMappingPhase.NeedsAttention => "Needs attention",
        RouterMappingPhase.Removing => "Undoing the router setup",
        RouterMappingPhase.Reconciling => "Rechecking the router setup",
        _ => "Not set up"
    };

    public static string Summary(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var summary = PhaseSummary(state);
        // A port an earlier version may have left open somewhere does not stop being open because a new
        // one was set up here, so it is said alongside whatever the current state is rather than only in
        // the states where nothing else is happening.
        return state.LegacyExposure is { } exposure ? $"{summary} {LegacyExposureNotice(exposure)}" : summary;
    }

    /// <summary>
    /// What an earlier version may have left behind, in the terms a person can act on: a port and the
    /// address that version wrote down, which is as much as ChunkPilot can honestly say.
    /// </summary>
    private static string LegacyExposureNotice(LegacyRouterExposure exposure)
    {
        var where = exposure.GatewayAddress.Length > 0
            ? $"a router it recorded as {exposure.GatewayAddress}"
            : "a router it did not record";
        var until = exposure.LeaseExpiresAt is null
            ? "ChunkPilot can't close it from here, so remove it in that router's settings if you want it closed."
            : "ChunkPilot can't close it from here; it runs out on its own, or you can remove it in that " +
              "router's settings.";
        return $"Separately: an earlier version of ChunkPilot may have left public port {exposure.ExternalPort} " +
               $"open on {where}, without recording which network connection reached it. {until}";
    }

    private static string PhaseSummary(RouterMappingState state)
    {
        return state.Phase switch
        {
            RouterMappingPhase.Off =>
                "Let friends connect through your router. ChunkPilot will ask your router to open the port for you.",
            RouterMappingPhase.Checking =>
                "Asking your router whether it can open the port by itself. Nothing has changed yet.",
            RouterMappingPhase.Inactive =>
                "Nothing is open on your router right now. The port opens when this server starts, and " +
                "closes again when it stops.",
            RouterMappingPhase.Supported =>
                "Your router answered and can open the port for you. Nothing has changed yet.",
            // "Unavailable" covers two genuinely different situations and they need different next
            // steps: the router said no, or ChunkPilot could not find a safe local address to forward to.
            RouterMappingPhase.Unavailable when state.Failure == RouterMappingFailure.NoGatewayFound =>
                "ChunkPilot couldn't work out a safe local address on a home router to send this port to. " +
                "Check that this computer is on your home network rather than only a VPN.",
            RouterMappingPhase.Unavailable =>
                "This router didn't offer automatic port forwarding. You can still set the port up yourself in your " +
                "router's own settings.",
            RouterMappingPhase.Undetermined =>
                "Your router didn't answer, so ChunkPilot can't tell whether it supports automatic setup. " +
                "You can try again, or set the port up yourself in your router's settings.",
            RouterMappingPhase.Creating =>
                "Asking your router to send this port to this computer.",
            RouterMappingPhase.Active => ActiveSummary(state),
            RouterMappingPhase.Conflict =>
                "Your router already sends this port somewhere else. ChunkPilot won't change a setting it didn't " +
                "create. Use a different server port, or remove the old entry in your router's settings.",
            RouterMappingPhase.NeedsAttention => AttentionSummary(state),
            RouterMappingPhase.Removing =>
                "Asking your router to stop sending this port to this computer.",
            RouterMappingPhase.Reconciling =>
                "Checking that the router setting ChunkPilot made is still there.",
            _ => "Let friends connect through your router."
        };
    }

    /// <summary>
    /// The upstream-network caveat has its own alert, so it is deliberately not repeated here: saying it
    /// twice in adjacent paragraphs reads as noise rather than as emphasis.
    /// </summary>
    private static string ActiveSummary(RouterMappingState state) =>
        "Your router is sending this port to this computer. " +
        "ChunkPilot can't confirm from here that the connection reaches the wider internet — " +
        "the surest test is a friend joining.";

    private static string AttentionSummary(RouterMappingState state) => state.OwnedNetworkUnknown
        ? "An earlier version of ChunkPilot opened this port without recording which router it used, so " +
          "ChunkPilot won't send anything that might change the wrong one. That port may still be open " +
          "on that router, and you can close it there. Setting up again opens the port on the network " +
          "this computer is on now."
        : state.RemovalPending
        ? "This server is stopped, but ChunkPilot couldn't confirm that the port it opened on your router " +
          "was closed again. It keeps trying, and you can try now or turn Direct internet off."
        : state.OwnedNetworkLeft
        ? "This computer is on a different network from the one ChunkPilot opened this port on, so that " +
          "opening can't be confirmed from here and isn't the address friends would use now. ChunkPilot " +
          "opens the port on this network when the server runs."
        : state.Failure switch
        {
            RouterMappingFailure.RequestRejected =>
                "Your router answered, but it turned down the request to open this port. Nothing was changed on it.",
            RouterMappingFailure.NotAuthorized =>
                "Your router refused the request. Automatic setup may be switched off in its settings.",
            RouterMappingFailure.OutOfResources =>
                "Your router said it has no room for another entry. Removing old entries in its settings may help.",
            RouterMappingFailure.NetworkFailure =>
                "Your router reported a network problem and didn't complete the request.",
            RouterMappingFailure.ServerPortUnavailable =>
                "This server has no usable port to open yet.",
            _ => "The last attempt didn't complete. You can try again."
        };

    /// <summary>
    /// Tone never overstates. An open router port is Info, not Success: it is a confirmed router action,
    /// not a confirmed connection.
    /// </summary>
    public static AppTone Tone(RouterMappingPhase phase) => phase switch
    {
        RouterMappingPhase.Active => AppTone.Info,
        // Set up and quiet. Nothing is open, and nothing is wrong.
        RouterMappingPhase.Inactive => AppTone.Neutral,
        RouterMappingPhase.Checking or RouterMappingPhase.Creating or RouterMappingPhase.Removing or
            RouterMappingPhase.Reconciling => AppTone.Accent,
        RouterMappingPhase.Conflict or RouterMappingPhase.NeedsAttention => AppTone.Warning,
        RouterMappingPhase.Unavailable or RouterMappingPhase.Undetermined => AppTone.Warning,
        RouterMappingPhase.Supported => AppTone.Info,
        _ => AppTone.Neutral
    };

    /// <summary>A short non-colour status word, so the state is readable without seeing the tone.</summary>
    public static string Badge(RouterMappingPhase phase) => phase switch
    {
        RouterMappingPhase.Off => "Off",
        RouterMappingPhase.Checking => "Checking",
        RouterMappingPhase.Supported => "Ready",
        RouterMappingPhase.Inactive => "Inactive",
        RouterMappingPhase.Unavailable => "Unavailable",
        RouterMappingPhase.Undetermined => "No answer",
        RouterMappingPhase.Creating => "Working",
        RouterMappingPhase.Active => "Port open",
        RouterMappingPhase.Conflict => "In use",
        RouterMappingPhase.NeedsAttention => "Attention",
        RouterMappingPhase.Removing => "Working",
        RouterMappingPhase.Reconciling => "Checking",
        _ => "Off"
    };

    public static AppIconKind Icon(RouterMappingPhase phase) => phase switch
    {
        RouterMappingPhase.Active => AppIconKind.Network,
        RouterMappingPhase.Conflict or RouterMappingPhase.NeedsAttention or
            RouterMappingPhase.Unavailable or RouterMappingPhase.Undetermined => AppIconKind.Warning,
        _ => AppIconKind.Network
    };

    /// <summary>The label for the primary action, which changes with what the state allows next.</summary>
    public static string PrimaryActionText(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            RouterMappingPhase.Off => "Set up automatically",
            RouterMappingPhase.Supported => "Set up automatically",
            RouterMappingPhase.Unavailable or RouterMappingPhase.Undetermined or
                RouterMappingPhase.Conflict or RouterMappingPhase.NeedsAttention => "Check again",
            _ => "Set up automatically"
        };
    }

    /// <summary>How the router's own claim about its internet address must be labelled.</summary>
    public static string AddressLabel() => "Router-reported address";

    public static string AddressCaveat(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RouterReportedAddressClass switch
        {
            RoutableAddressClass.SharedAddressSpace or RoutableAddressClass.PrivateUse =>
                "Your router appears to be behind another network layer, so this address may not reach your home " +
                "from the internet.",
            RoutableAddressClass.Unknown =>
                "Your router didn't say what its internet address is.",
            _ => "This is what your router says its internet address is. ChunkPilot has not verified it from outside."
        };
    }

    /// <summary>
    /// Which mechanism is involved, distinguishing one that owns a mapping from one that merely
    /// answered. Reporting "None established" while the detail line quotes the router's own reply was
    /// what made the failed real-router attempt impossible to read.
    /// </summary>
    public static string MechanismLabel(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Mechanism != RouterMappingMechanism.None)
            return MechanismLabel(state.Mechanism);
        return state.AvailableMechanism == RouterMappingMechanism.None
            ? "None established"
            : $"{MechanismLabel(state.AvailableMechanism)} — answered, no mapping created";
    }

    /// <summary>Expert-only wording. Protocol names belong here and nowhere else.</summary>
    public static string MechanismLabel(RouterMappingMechanism mechanism) => mechanism switch
    {
        RouterMappingMechanism.Pcp => "PCP (RFC 6887)",
        RouterMappingMechanism.NatPmp => "NAT-PMP (RFC 6886)",
        RouterMappingMechanism.UpnpIgd => "UPnP IGD",
        _ => "None established"
    };

    public static string TransportLabel(MappingTransport transport) =>
        transport == MappingTransport.Tcp ? "TCP" : "UDP";

    /// <summary>
    /// A lease only describes something that exists. Reporting an expiry for a mapping that is not
    /// active would be exactly the kind of leftover value that reads as a live fact.
    /// </summary>
    public static string LeaseLabel(RouterMappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != RouterMappingPhase.Active)
            return "Not established";
        if (!state.LeaseIsFinite)
            return "Permanent — this router does not offer timed entries";
        return state.LeaseExpiresAt is { } expires
            ? $"Renews automatically; current entry expires {expires.ToLocalTime():h:mm:ss tt}"
            : "Not established";
    }

    public static string AddressClassLabel(RoutableAddressClass value) => value switch
    {
        RoutableAddressClass.GloballyRoutable => "Globally routable",
        RoutableAddressClass.PrivateUse => "Private use (RFC 1918)",
        RoutableAddressClass.SharedAddressSpace => "Shared address space (RFC 6598)",
        RoutableAddressClass.Loopback => "Loopback",
        RoutableAddressClass.LinkLocal => "Link-local",
        RoutableAddressClass.Documentation => "Documentation range",
        RoutableAddressClass.Reserved => "Reserved",
        _ => "Not reported"
    };

    /// <summary>
    /// The consent text shown once, before the first router change for this server. Four facts, no wall.
    /// </summary>
    public static IReadOnlyList<string> ConsentPoints(int port) =>
    [
        $"ChunkPilot will ask your router to send port {port} to this computer.",
        "That makes this port reachable from outside your home network.",
        "ChunkPilot removes its own entry when you turn Direct internet off or stop this server normally.",
        "This does not guarantee the connection works from the wider internet."
    ];
}
