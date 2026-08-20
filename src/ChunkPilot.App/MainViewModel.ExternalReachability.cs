using System.Globalization;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

/// <summary>
/// External access: the third and last layer of Direct internet, and the only one that can produce
/// evidence from outside this home network.
/// </summary>
/// <remarks>
/// <para>
/// This partial holds presentation and one deliberate user intent. It contains no HTTP, no service
/// address, no JSON and no retry policy: the Agent owns the check, Infrastructure owns the transport,
/// and the ViewModel asks and renders what comes back.
/// </para>
/// <para>
/// Nothing here contacts anything remote on its own. Selecting a server, opening Overview, refreshing
/// and reconnecting all read the Agent's local state over the named pipe;
/// <see cref="CheckExternalReachabilityCommand"/> is the only path that reaches the probe service, and
/// it exists only behind a button somebody has to press.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityTitle))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilitySummary))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityTone))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityBadge))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityIcon))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityActionText))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityFirstUseNotice))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityFirstUseNotice))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityCheckAction))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityCancel))]
    [NotifyPropertyChangedFor(nameof(IsExternalReachabilityCheckEnabled))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityVerifiedAt))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityVerifiedDetail))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityAddressComparison))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityAddressComparison))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityUpstreamAssessment))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityUpstreamAssessment))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityObservedAddress))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityRouterAddress))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityPortLabel))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityConnectTimeLabel))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityCheckedAtLabel))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityEndpointLabel))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityTechnicalDetail))]
    [NotifyPropertyChangedFor(nameof(PublicAccessVerified))]
    [NotifyPropertyChangedFor(nameof(PublicAccessVerifiedEndpoint))]
    [NotifyPropertyChangedFor(nameof(PublicAccessVerifiedCaption))]
    [NotifyPropertyChangedFor(nameof(ShowsPublicAccessNotVerifiedCaveat))]
    [NotifyPropertyChangedFor(nameof(FirewallCombinedStatus))]
    private ExternalReachabilityState externalReachability = new();

    public string ExternalReachabilityTitle =>
        ExternalReachabilityPresentation.Title(ExternalReachability);

    public string ExternalReachabilitySummary =>
        ExternalReachabilityPresentation.Summary(ExternalReachability, RouterMapping, FirewallAccess);

    public AppTone ExternalReachabilityTone => ExternalReachabilityPresentation.Tone(ExternalReachability);
    public string ExternalReachabilityBadge => ExternalReachabilityPresentation.Badge(ExternalReachability);
    public AppIconKind ExternalReachabilityIcon => ExternalReachabilityPresentation.Icon(ExternalReachability);
    public string ExternalReachabilityActionText =>
        ExternalReachabilityPresentation.ActionText(ExternalReachability);

    /// <summary>
    /// The compact first-use explanation. It appears before the first deliberate probe for this
    /// server and disappears once a result exists, which is exactly the moment it stops being new
    /// information — no modal, no dismissal state to remember, and nothing persisted.
    /// </summary>
    public string ExternalReachabilityFirstUseNotice =>
        ExternalReachabilityPresentation.FirstUseNotice(ExternalReachability);

    public bool ShowsExternalReachabilityFirstUseNotice =>
        ExternalReachability.ProbeConfigured && ExternalReachability.CanCheck &&
        ExternalReachability.Phase == ExternalReachabilityPhase.NotChecked;

    public bool ShowsExternalReachabilityCheckAction => !ExternalReachability.Busy;

    /// <summary>
    /// Offered but disabled when a prerequisite is missing, so the control explains itself rather
    /// than vanishing. What is missing is already named in the summary above it.
    /// </summary>
    public bool IsExternalReachabilityCheckEnabled => ExternalReachability.CanCheck;

    public bool ShowsExternalReachabilityCancel => ExternalReachability.Busy;

    public string ExternalReachabilityVerifiedAt =>
        ExternalReachabilityPresentation.VerifiedAt(ExternalReachability);

    public bool ShowsExternalReachabilityVerifiedDetail => ExternalReachability.IsVerified;

    public bool ShowsExternalReachabilityAddressComparison =>
        ExternalReachability.Phase == ExternalReachabilityPhase.SourceAddressMismatch;

    public string ExternalReachabilityAddressComparison =>
        ExternalReachabilityPresentation.AddressComparison(ExternalReachability);

    public string ExternalReachabilityUpstreamAssessment =>
        ExternalReachabilityPresentation.UpstreamAssessment(ExternalReachability, RouterMapping);

    public bool ShowsExternalReachabilityUpstreamAssessment =>
        ExternalReachabilityUpstreamAssessment.Length > 0;

    // ── Technical details. Evidence only; never a guess. ──

    public string ExternalReachabilityObservedAddress => ExternalReachability.ObservedAddress.Length > 0
        ? ExternalReachability.ObservedAddress
        : "Not reported";

    public string ExternalReachabilityRouterAddress => ExternalReachability.RouterReportedAddress.Length > 0
        ? ExternalReachability.RouterReportedAddress
        : "Not reported";

    public string ExternalReachabilityPortLabel => ExternalReachability.Port > 0
        ? $"TCP {ExternalReachability.Port.ToString(CultureInfo.InvariantCulture)}"
        : "Not established";

    public string ExternalReachabilityConnectTimeLabel => ExternalReachability.IsVerified &&
        ExternalReachability.ConnectMilliseconds > 0
        ? $"{ExternalReachability.ConnectMilliseconds.ToString(CultureInfo.InvariantCulture)} ms"
        : "Not measured";

    public string ExternalReachabilityCheckedAtLabel =>
        ExternalReachabilityPresentation.CheckedAtLabel(ExternalReachability);

    /// <summary>The exact identity a stored result belongs to, so a stale claim can be seen to be stale.</summary>
    public string ExternalReachabilityEndpointLabel =>
        ExternalReachability.CheckedEndpoint.IsComplete
            ? $"{ExternalReachability.CheckedEndpoint.PublicAddress}:" +
              $"{ExternalReachability.CheckedEndpoint.ExternalPort.ToString(CultureInfo.InvariantCulture)} " +
              $"→ {ExternalReachability.CheckedEndpoint.MappingIdentity} " +
              $"(run {ExternalReachability.CheckedEndpoint.RunIdentity})"
            : "No result is bound to an endpoint.";

    public string ExternalReachabilityTechnicalDetail => ExternalReachability.LastOperationDetail.Length > 0
        ? ExternalReachability.LastOperationDetail
        : "No external check has run for this server.";

    // ── The Connect card's Public access tile ──

    /// <summary>The only condition under which the Connect card may show a verified public endpoint.</summary>
    public bool PublicAccessVerified => ExternalReachability.IsVerified &&
                                        ExternalReachability.VerifiedEndpoint.Length > 0;

    public string PublicAccessVerifiedEndpoint => ExternalReachability.VerifiedEndpoint;

    public string PublicAccessVerifiedCaption => ExternalReachabilityVerifiedAt;

    /// <summary>
    /// The pre-verification caveat. It stops the moment there is genuine evidence, and it is not
    /// shown alongside a verified endpoint, which would contradict it.
    /// </summary>
    public bool ShowsPublicAccessNotVerifiedCaveat => HasConfiguredPublicAddress && !PublicAccessVerified;

    /// <summary>
    /// The one command that leaves this computer, and it does so once per press.
    /// </summary>
    /// <remarks>
    /// The answer can take seconds, and a person is free to walk to another server while it is out.
    /// The server this press belongs to is therefore captured up front and the answer is published
    /// only if it still belongs where it landed — see <see cref="TryApplyExternalReachability"/>.
    /// </remarks>
    [RelayCommand]
    private async Task CheckExternalReachabilityAsync()
    {
        if (SelectedServer is null || ExternalReachability.Busy)
            return;
        var serverId = SelectedServer.Definition.Id;
        // The Agent decides eligibility; asking anyway would be the App inventing a state. This only
        // avoids a pointless round trip for a state the App already has in hand.
        if (!ExternalReachability.CanCheck)
        {
            StatusMessage = ExternalReachabilityTitle;
            return;
        }
        var sequence = ClaimExternalReachabilityUpdate();
        await RunBusyAsync("Checking from outside…", async () =>
        {
            var state = await client.SendAsync<ExternalReachabilityState>(
                "CheckExternalReachability",
                ConnectivityRequest(serverId, PublicConnectivityOperation.CheckExternalReachability))
                .ConfigureAwait(true);
            if (TryApplyExternalReachability(state, serverId, sequence, supersedeOnApply: true))
                StatusMessage = ExternalReachabilityTitle;
        }, failureAction: "External check failed").ConfigureAwait(true);
    }

    /// <summary>
    /// Stops the check that is actually running, which is not the same thing as the server on screen.
    /// </summary>
    /// <remarks>
    /// The owner is taken from the authoritative state being displayed rather than from the current
    /// selection. Cancel is only ever offered while that state reports a check in flight, so this
    /// cannot reach a server the user merely happens to be looking at, and a check started for one
    /// server can never be stopped by pressing Cancel in front of another.
    /// </remarks>
    [RelayCommand]
    private async Task CancelExternalReachabilityAsync()
    {
        var owner = ExternalReachability.Busy ? ExternalReachability.ServerId : Guid.Empty;
        if (owner == Guid.Empty)
            return;
        var sequence = ClaimExternalReachabilityUpdate();
        var state = await client.SendAsync<ExternalReachabilityState>(
            "CancelExternalReachability",
            ConnectivityRequest(owner, PublicConnectivityOperation.CancelExternalReachability))
            .ConfigureAwait(true);
        if (TryApplyExternalReachability(state, owner, sequence, supersedeOnApply: true))
            StatusMessage = "External check cancelled.";
    }

    [RelayCommand]
    private void CopyVerifiedPublicEndpoint()
    {
        if (!PublicAccessVerified)
            return;
        System.Windows.Clipboard.SetText(PublicAccessVerifiedEndpoint);
        StatusMessage = "Verified public address copied. " +
                        "It answered from outside your network when it was checked.";
    }

    [RelayCommand]
    private void CopyExternalReachabilityTechnicalDetails()
    {
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
            $"External reachability: {ExternalReachability.Phase}",
            $"Prerequisite: {ExternalReachability.Blocker}",
            $"Probe configured: {ExternalReachability.ProbeConfigured}",
            $"Router-reported address: {ExternalReachabilityRouterAddress}",
            $"Externally observed address: {ExternalReachabilityObservedAddress}",
            $"Port: {ExternalReachabilityPortLabel}",
            $"Connect time: {ExternalReachabilityConnectTimeLabel}",
            $"Checked at: {ExternalReachabilityCheckedAtLabel}",
            $"Checked endpoint: {ExternalReachabilityEndpointLabel}",
            $"Service result: {ExternalReachabilityTechnicalDetail}"));
        StatusMessage = "External check details copied.";
    }

    /// <summary>
    /// Marks every external reachability read already in flight as superseded and returns the identity
    /// the claiming operation will be judged against when its own answer arrives.
    /// </summary>
    private int ClaimExternalReachabilityUpdate() => Interlocked.Increment(ref externalReachabilitySequence);

    private int externalReachabilitySequence;

    /// <summary>
    /// Publishes an answer only if it still belongs to the workspace that is on screen.
    /// </summary>
    /// <param name="serverId">The server the operation was started for, captured before it was sent.</param>
    /// <param name="sequence">
    /// The value of <see cref="externalReachabilitySequence"/> this operation was issued under.
    /// </param>
    /// <param name="supersedeOnApply">
    /// True for a deliberate command, which publishes an answer somebody waited for and therefore
    /// invalidates every background read that began while it was waiting. False for a background read,
    /// which must never invalidate the command whose result the user actually asked for.
    /// </param>
    /// <remarks>
    /// <para>
    /// Three things have to still hold, and none of them implies the others: the Agent answered about
    /// the server that asked, that server is still the one being displayed, and nothing newer has
    /// replaced this operation. Failing any of them the answer is dropped in silence — an external
    /// reachability state carries a verified public endpoint that can be read and copied, so showing
    /// one server's under another is a truthfulness failure, not a cosmetic flicker.
    /// </para>
    /// <para>
    /// Ownership is exact, and an unidentified answer is not a wildcard. Every state the Agent produces
    /// names the server it is about, so an empty id is not a state that belongs to everybody — it is a
    /// default-constructed or otherwise unattributable payload, and admitting one here would let a
    /// malformed answer overwrite a real server's visible reachability with something no server
    /// vouched for.
    /// </para>
    /// </remarks>
    private bool TryApplyExternalReachability(
        ExternalReachabilityState state, Guid serverId, int sequence, bool supersedeOnApply)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (serverId == Guid.Empty || state.ServerId != serverId)
            return false;
        if (SelectedServer?.Definition.Id != serverId)
            return false;
        if (Volatile.Read(ref externalReachabilitySequence) != sequence)
            return false;
        if (supersedeOnApply)
            _ = ClaimExternalReachabilityUpdate();
        ExternalReachability = state;
        return true;
    }

    /// <summary>Reads the Agent's local state. Contacts no remote service.</summary>
    private async Task LoadExternalReachabilityAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var sequence = Volatile.Read(ref externalReachabilitySequence);
        var state = await client.SendAsync<ExternalReachabilityState>(
            "GetExternalReachability",
            ConnectivityRequest(serverId, PublicConnectivityOperation.ReadExternalReachability))
            .ConfigureAwait(true);
        _ = TryApplyExternalReachability(state, serverId, sequence, supersedeOnApply: false);
    }
}
