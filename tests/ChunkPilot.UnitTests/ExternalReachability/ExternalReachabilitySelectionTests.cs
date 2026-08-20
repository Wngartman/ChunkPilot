using ChunkPilot.App;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// Which workspace an external reachability answer is allowed to land in.
/// </summary>
/// <remarks>
/// An external check takes seconds and a person is free to walk to another server while it is out.
/// The state it produces carries a verified public endpoint that can be read and copied, so showing
/// one server's answer under another is a truthfulness failure rather than a cosmetic flicker. Every
/// test here holds a check open on purpose and moves the selection underneath it.
/// </remarks>
public sealed class ExternalReachabilitySelectionTests
{
    private static readonly Guid ServerA = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly Guid ServerB = Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");
    private const string PublicA = "93.184.216.34";
    private const string PublicB = "198.41.128.9";

    // ── The ordinary case still works ──

    [Fact]
    public async Task A_result_is_displayed_when_its_own_server_is_still_selected()
    {
        var (model, client) = await ReadyAsync();
        client.States[ServerA] = Verified(ServerA);

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.True(model.PublicAccessVerified);
        Assert.Equal($"{PublicA}:25566", model.PublicAccessVerifiedEndpoint);
        Assert.Equal(ServerA, client.LastRequest("CheckExternalReachability"));
    }

    // ── The defect ──

    [Fact]
    public async Task A_result_that_returns_after_the_user_moved_on_never_appears_under_another_server()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await SelectAsync(model, ServerB);
        Assert.Equal(ServerB, model.ExternalReachability.ServerId);

        gate.SetResult(Verified(ServerA));
        await check;

        // B is still B, and nothing of A's is on screen or on the clipboard path.
        Assert.Equal(ServerB, model.SelectedServer!.Definition.Id);
        Assert.Equal(ServerB, model.ExternalReachability.ServerId);
        Assert.False(model.PublicAccessVerified);
        Assert.Equal("", model.PublicAccessVerifiedEndpoint);
        Assert.DoesNotContain(PublicA, model.ExternalReachabilityObservedAddress, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicA, model.ExternalReachabilityRouterAddress, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coming back before the answer lands is the one case where a late result is still the right
    /// answer for what is on screen, so it is published.
    /// </summary>
    [Fact]
    public async Task Returning_to_the_server_before_its_check_finishes_still_shows_its_own_result()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await SelectAsync(model, ServerB);
        await SelectAsync(model, ServerA);

        gate.SetResult(Verified(ServerA));
        await check;

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.True(model.PublicAccessVerified);
        Assert.Equal($"{PublicA}:25566", model.PublicAccessVerifiedEndpoint);
    }

    [Fact]
    public async Task A_second_server_checking_for_itself_never_inherits_the_first_servers_answer()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await SelectAsync(model, ServerB);
        gate.SetResult(Verified(ServerA));
        await check;
        Assert.False(model.PublicAccessVerified);

        client.States[ServerB] = Verified(ServerB);
        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerB, model.ExternalReachability.ServerId);
        Assert.Equal($"{PublicB}:25567", model.PublicAccessVerifiedEndpoint);
        Assert.Equal(ServerB, client.LastRequest("CheckExternalReachability"));
    }

    [Fact]
    public async Task A_result_is_discarded_when_nothing_is_selected_any_more()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        model.SelectedServer = null;

        gate.SetResult(Verified(ServerA));
        await check;

        Assert.Null(model.SelectedServer);
        Assert.False(model.PublicAccessVerified);
    }

    /// <summary>
    /// A newer deliberate operation supersedes an older one for the same server, so the answer the
    /// user last asked for is the one that stands.
    /// </summary>
    [Fact]
    public async Task An_answer_superseded_by_a_newer_operation_is_discarded()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        // The Agent reports the check as running, which is what puts Cancel on screen.
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.ShowsExternalReachabilityCancel);

        client.States[ServerA] = Ready(ServerA) with { Phase = ExternalReachabilityPhase.Cancelled };
        client.ReleaseCheckHold(ServerA);
        await model.CancelExternalReachabilityCommand.ExecuteAsync(null);

        gate.SetResult(Verified(ServerA));
        await check;

        Assert.Equal(ExternalReachabilityPhase.Cancelled, model.ExternalReachability.Phase);
        Assert.False(model.PublicAccessVerified);
    }

    // ── Genuinely overlapping operations ──
    //
    // Two deliberate checks can never be out at once: RunBusyAsync admits one busy operation at a
    // time for the whole workspace, so pressing Check for a second server while the first is still
    // out sends nothing at all. What genuinely overlaps a check in flight is the background reading
    // that selecting and refreshing do, and that is what these hold open on purpose.

    [Fact]
    public async Task A_second_servers_check_cannot_even_start_while_another_servers_check_is_out()
    {
        var (model, client) = await ReadyAsync();
        var gateA = client.HoldCheck(ServerA);

        var checkA = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        client.ReleaseCheckHold(ServerA);
        await SelectAsync(model, ServerB);
        Assert.False(checkA.IsCompleted);

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        // One deliberate operation at a time: B's press reached nothing, so there is no second answer
        // to land in the wrong place.
        Assert.Equal(1, client.Operations.Count(operation =>
            operation.Equals("CheckExternalReachability", StringComparison.Ordinal)));
        gateA.SetResult(Verified(ServerA));
        await checkA;
        Assert.Equal(ServerB, model.ExternalReachability.ServerId);
        Assert.False(model.PublicAccessVerified);
    }

    /// <summary>
    /// The overlap that really happens: B's own state is loaded and displayed while A's check is still
    /// out, and A's answer then lands on top of it.
    /// </summary>
    [Fact]
    public async Task A_late_answer_never_displaces_the_verified_endpoint_the_new_server_established()
    {
        var (model, client) = await ReadyAsync();
        var gateA = client.HoldCheck(ServerA);

        var checkA = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        client.ReleaseCheckHold(ServerA);
        await SelectAsync(model, ServerB);

        // B has a verified result of its own, read in the background while A's check is still out.
        client.States[ServerB] = Verified(ServerB);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal($"{PublicB}:25567", model.PublicAccessVerifiedEndpoint);
        Assert.False(checkA.IsCompleted);

        gateA.SetResult(Verified(ServerA));
        await checkA;

        Assert.Equal(ServerB, model.ExternalReachability.ServerId);
        Assert.True(model.PublicAccessVerified);
        Assert.Equal($"{PublicB}:25567", model.PublicAccessVerifiedEndpoint);
        Assert.DoesNotContain(PublicA, model.ExternalReachabilityObservedAddress, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicA, model.ExternalReachabilityRouterAddress, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two operations for the *same* server, overlapping. The older one is not for another workspace —
    /// it is simply stale, and the newer answer is the one somebody actually waited for.
    /// </summary>
    [Fact]
    public async Task An_older_read_for_the_same_server_cannot_overwrite_a_newer_deliberate_answer()
    {
        var (model, client) = await ReadyAsync();
        var reads = client.RequestCount("GetExternalReachability", ServerA);
        var stale = client.HoldLoad(ServerA);

        // A background read for A goes out and is held open.
        var refresh = model.RefreshCommand.ExecuteAsync(null);
        await client.WaitForRequestAsync("GetExternalReachability", ServerA, reads + 1);
        Assert.False(refresh.IsCompleted);

        // The user then presses Check for the same server and that answer arrives first.
        client.States[ServerA] = Verified(ServerA);
        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        Assert.True(model.PublicAccessVerified);

        // The older read finally answers, contradicting it.
        stale.SetResult(Ready(ServerA) with { Phase = ExternalReachabilityPhase.Unreachable });
        await refresh;

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.Equal(ExternalReachabilityPhase.Reachable, model.ExternalReachability.Phase);
        Assert.Equal($"{PublicA}:25566", model.PublicAccessVerifiedEndpoint);
    }

    // ── Ownership of the answer itself ──

    /// <summary>
    /// An unattributed answer is not a wildcard. Every state the Agent produces names its server, so an
    /// empty id means the payload cannot be placed — and an unplaceable payload must never be shown.
    /// </summary>
    [Fact]
    public async Task An_answer_carrying_no_server_id_is_refused_rather_than_treated_as_anyones()
    {
        var (model, client) = await ReadyAsync();
        client.States[ServerA] = Verified(ServerA) with { ServerId = Guid.Empty };

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.False(model.PublicAccessVerified);
        Assert.Equal("", model.PublicAccessVerifiedEndpoint);
    }

    [Fact]
    public async Task A_default_constructed_answer_cannot_change_what_is_on_screen()
    {
        var (model, client) = await ReadyAsync();
        client.States[ServerA] = new ExternalReachabilityState();

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.True(model.ExternalReachability.ProbeConfigured);
        Assert.False(model.PublicAccessVerified);
    }

    [Fact]
    public async Task An_answer_about_a_different_server_is_refused()
    {
        var (model, client) = await ReadyAsync();
        client.States[ServerA] = Verified(ServerB);

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerA, model.ExternalReachability.ServerId);
        Assert.False(model.PublicAccessVerified);
        Assert.DoesNotContain(PublicB, model.ExternalReachabilityObservedAddress, StringComparison.Ordinal);
    }

    // ── Cancellation ownership ──

    [Fact]
    public async Task Cancel_targets_the_server_whose_check_is_running_not_the_one_on_screen()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await SelectAsync(model, ServerB);

        // Nothing is running for B, so there is nothing on screen to cancel and nothing is sent.
        Assert.False(model.ShowsExternalReachabilityCancel);
        await model.CancelExternalReachabilityCommand.ExecuteAsync(null);
        Assert.DoesNotContain("CancelExternalReachability", client.Operations, StringComparer.Ordinal);

        // Back in front of the running check, Cancel reaches the server that owns it.
        await SelectAsync(model, ServerA);
        Assert.True(model.ShowsExternalReachabilityCancel);
        client.ReleaseCheckHold(ServerA);
        await model.CancelExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(ServerA, client.LastRequest("CancelExternalReachability"));
        gate.SetResult(Verified(ServerA));
        await check;
    }

    [Fact]
    public async Task Cancel_never_reaches_a_server_that_is_merely_selected()
    {
        var (model, client) = await ReadyAsync();
        await SelectAsync(model, ServerB);

        await model.CancelExternalReachabilityCommand.ExecuteAsync(null);

        Assert.DoesNotContain("CancelExternalReachability", client.Operations, StringComparer.Ordinal);
    }

    // ── Existing behaviour that must survive ──

    [Fact]
    public async Task A_second_press_while_a_check_is_running_still_sends_nothing()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await model.RefreshCommand.ExecuteAsync(null);
        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(1, client.Operations.Count(operation =>
            operation.Equals("CheckExternalReachability", StringComparison.Ordinal)));
        gate.SetResult(Verified(ServerA));
        await check;
    }

    [Fact]
    public async Task A_transport_failure_while_a_check_is_pending_publishes_nothing()
    {
        var (model, client) = await ReadyAsync();
        var gate = client.HoldCheck(ServerA);

        var check = model.CheckExternalReachabilityCommand.ExecuteAsync(null);
        await client.WaitForCheckAsync(ServerA);
        await SelectAsync(model, ServerB);
        gate.SetException(new IOException("the pipe was closed"));
        await check;

        Assert.Equal(ServerB, model.ExternalReachability.ServerId);
        Assert.False(model.PublicAccessVerified);
        Assert.Contains("pipe", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Harness ──

    private static async Task<(MainViewModel Model, TwoServerClient Client)> ReadyAsync()
    {
        var client = new TwoServerClient();
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        await SelectAsync(model, ServerA);
        return (model, client);
    }

    private static async Task SelectAsync(MainViewModel model, Guid serverId)
    {
        model.SelectedServer = model.Servers.First(server => server.Definition.Id == serverId);
        // The workspace load is fire-and-forget from the selection setter; let it settle.
        for (var attempt = 0; attempt < 400 && model.ExternalReachability.ServerId != serverId; attempt++)
            await Task.Delay(10);
        Assert.Equal(serverId, model.ExternalReachability.ServerId);
    }

    private static ExternalReachabilityState Ready(Guid serverId) => new()
    {
        ServerId = serverId,
        Phase = ExternalReachabilityPhase.NotChecked,
        ProbeConfigured = true,
        Endpoint = Endpoint(serverId),
        RouterReportedAddress = Address(serverId),
        Port = Port(serverId)
    };

    private static ExternalReachabilityState Verified(Guid serverId) => Ready(serverId) with
    {
        Phase = ExternalReachabilityPhase.Reachable,
        CheckedEndpoint = Endpoint(serverId),
        ObservedAddress = Address(serverId),
        ConnectMilliseconds = 118,
        CheckedAt = new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero)
    };

    private static ExternalReachabilityEndpoint Endpoint(Guid serverId) => new()
    {
        ServerId = serverId,
        PublicAddress = Address(serverId),
        ExternalPort = Port(serverId),
        InternalPort = Port(serverId),
        MappingIdentity = $"{serverId:N}:UpnpIgd/Tcp/10.0.0.140:{Port(serverId)}->{Port(serverId)}",
        RunIdentity = $"4114@63800000000000000{(serverId == ServerA ? 0 : 1)}"
    };

    private static string Address(Guid serverId) => serverId == ServerA ? PublicA : PublicB;
    private static int Port(Guid serverId) => serverId == ServerA ? 25566 : 25567;

    /// <summary>
    /// Two servers, and a check that can be held open so the selection can be moved underneath it.
    /// </summary>
    private sealed class TwoServerClient : IAgentClient
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, TaskCompletionSource<ExternalReachabilityState>> held = [];
        private readonly Dictionary<Guid, TaskCompletionSource<ExternalReachabilityState>> heldLoads = [];
        private readonly HashSet<Guid> reportedBusy = [];
        private readonly List<(string Operation, Guid ServerId)> requests = [];

        public Dictionary<Guid, ExternalReachabilityState> States { get; } = new()
        {
            [ServerA] = Ready(ServerA),
            [ServerB] = Ready(ServerB)
        };

        public IReadOnlyList<string> Operations
        {
            get { lock (gate) return requests.Select(request => request.Operation).ToArray(); }
        }

        public Guid LastRequest(string operation)
        {
            lock (gate)
                return requests.LastOrDefault(request =>
                    request.Operation.Equals(operation, StringComparison.Ordinal)).ServerId;
        }

        /// <summary>Holds one check open, and reports it as running to every read until released.</summary>
        public TaskCompletionSource<ExternalReachabilityState> HoldCheck(Guid serverId)
        {
            var source = new TaskCompletionSource<ExternalReachabilityState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                held[serverId] = source;
                reportedBusy.Add(serverId);
            }
            return source;
        }

        /// <summary>Holds the next background state read for one server open.</summary>
        public TaskCompletionSource<ExternalReachabilityState> HoldLoad(Guid serverId)
        {
            var source = new TaskCompletionSource<ExternalReachabilityState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
                heldLoads[serverId] = source;
            return source;
        }

        /// <summary>Stops reporting the check as running without completing it.</summary>
        public void ReleaseCheckHold(Guid serverId)
        {
            lock (gate)
                reportedBusy.Remove(serverId);
        }

        public int RequestCount(string operation, Guid serverId)
        {
            lock (gate)
                return requests.Count(request =>
                    request.Operation.Equals(operation, StringComparison.Ordinal) &&
                    request.ServerId == serverId);
        }

        public Task WaitForCheckAsync(Guid serverId) =>
            WaitForRequestAsync("CheckExternalReachability", serverId, 1);

        public async Task WaitForRequestAsync(string operation, Guid serverId, int minimum)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                if (RequestCount(operation, serverId) >= minimum)
                    return;
                await Task.Delay(10);
            }
            Assert.Fail($"Fewer than {minimum} {operation} requests were sent for {serverId}.");
        }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            var serverId = payload is ServerIdRequest request ? request.ServerId : Guid.Empty;
            TaskCompletionSource<ExternalReachabilityState>? pending = null;
            lock (gate)
            {
                requests.Add((operation, serverId));
                if (operation == "CheckExternalReachability")
                    held.Remove(serverId, out pending);
                else if (operation == "GetExternalReachability")
                    heldLoads.Remove(serverId, out pending);
            }
            if (pending is not null)
                return (TResponse)(object)await pending.Task.ConfigureAwait(false);
            return (TResponse)Respond(operation, serverId);
        }

        private object Respond(string operation, Guid serverId) => operation switch
        {
            "Dashboard" => new DashboardSnapshot
            {
                AgentConnected = true,
                Host = new HostSnapshot { LanAddress = "10.0.0.140" },
                Servers = [Snapshot(ServerA), Snapshot(ServerB)]
            },
            "GetExternalReachability" or "CheckExternalReachability" or "CancelExternalReachability" =>
                Reachability(serverId),
            "GetRouterMapping" or "CheckRouterMapping" or "EnableRouterMapping" or
                "DisableRouterMapping" or "CancelRouterMapping" or "RetryRouterMapping" =>
                new RouterMappingState { ServerId = serverId },
            "GetFirewallAccess" or "CheckFirewallAccess" or "CancelFirewallAccess" =>
                new WindowsFirewallState { ServerId = serverId },
            "GetCapabilities" => new ServerCapabilityProfile(),
            // Direct internet is the selected method, which is what puts External access on screen and
            // makes its state worth re-reading on a refresh.
            "GetNetworkConfiguration" => new NetworkConfiguration { Mode = NetworkMode.PortForwarding },
            "ListBackups" => Array.Empty<BackupRecord>(),
            "ListSchedules" => Array.Empty<ScheduleEntry>(),
            "ListFiles" => Array.Empty<FileSystemEntry>(),
            "Inventory" => Array.Empty<ModPluginEntry>(),
            "Diagnostics" => Array.Empty<DiagnosticFinding>(),
            "ListWorlds" => Array.Empty<WorldEntry>(),
            "ListWhitelist" => Array.Empty<WhitelistEntry>(),
            "ListPlayerAccess" => Array.Empty<UnifiedPlayerAccess>(),
            "GetPlayerAccess" => new PlayerAccessSnapshot(),
            "ReadGamerules" => new GameruleStateResponse(),
            "ListAutomationRecipes" => Array.Empty<AutomationRecipe>(),
            "GetCrossplayConfiguration" => new CrossplayConfiguration(),
            "ListDatapacks" => Array.Empty<DatapackInventoryItem>(),
            "GetResourcePackConfiguration" => new ResourcePackConfiguration(),
            "GetSetting" => new TextResponse(""),
            "GetUpdateSource" => (object?)null!,
            "GetUpdatePreferences" => new UpdatePreferences(),
            "ListVersions" => Array.Empty<VersionSnapshot>(),
            "ListUpdateHistory" => Array.Empty<UpdateHistoryEntry>(),
            _ => OperationResult.Ok("ok")
        };

        private ExternalReachabilityState Reachability(Guid serverId)
        {
            lock (gate)
            {
                var state = States.TryGetValue(serverId, out var known) ? known : Ready(serverId);
                return reportedBusy.Contains(serverId)
                    ? state with { Phase = ExternalReachabilityPhase.Checking, Busy = true }
                    : state;
            }
        }

        private static ServerSnapshot Snapshot(Guid serverId) => new()
        {
            Definition = new ServerDefinition
            {
                Id = serverId,
                Name = serverId == ServerA ? "Server A" : "Server B",
                RootPath = serverId == ServerA ? @"C:\fixture-a" : @"C:\fixture-b",
                Port = Port(serverId)
            },
            State = ServerState.Running
        };
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
