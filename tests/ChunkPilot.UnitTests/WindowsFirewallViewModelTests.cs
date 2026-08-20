using ChunkPilot.App;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class WindowsFirewallViewModelTests
{
    [Fact]
    public void Public_profile_is_specific_consent_with_exact_target_details()
    {
        var client = new FirewallClient();
        var model = Model(client, new RecordingLauncher(FirewallElevationOutcome.Completed(0, "unused")));
        model.FirewallAccess = client.State(FirewallAccessPhase.PublicNetworkConfirmationRequired) with
        {
            Profiles = FirewallProfile.Public,
            SelectedProfile = FirewallProfile.Public,
            Category = WindowsNetworkCategory.Public,
            InterfaceName = "Ethernet",
            NetworkName = "NootsBoots"
        };

        Assert.Equal("Windows considers this a Public network", model.FirewallTitle);
        Assert.Equal("Ethernet", model.FirewallNetworkDisplay);
        Assert.Contains("Ethernet", model.FirewallSummary, StringComparison.Ordinal);
        Assert.Contains("Public network", model.FirewallConsentTitle, StringComparison.Ordinal);
        Assert.Equal(25566, model.FirewallAccess.Port);
        Assert.Equal(client.Java, model.FirewallAccess.ProgramPath);
    }

    [Fact]
    public void Opening_consent_is_local_and_does_not_contact_the_agent_or_elevate()
    {
        var client = new FirewallClient();
        var launcher = new RecordingLauncher(FirewallElevationOutcome.Completed(0, "unused"));
        var model = Model(client, launcher);

        model.RequestFirewallAccessCommand.Execute(null);

        Assert.True(model.ShowsFirewallConsent);
        Assert.Empty(client.Operations);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task Confirm_passes_agent_built_arguments_unchanged_and_correlates_completion()
    {
        var client = new FirewallClient();
        var launcher = new RecordingLauncher(FirewallElevationOutcome.Completed(0, "fixture helper"));
        var model = Model(client, launcher);
        model.RequestFirewallAccessCommand.Execute(null);

        await model.ConfirmFirewallAccessCommand.ExecuteAsync(null);

        Assert.Equal(1, launcher.Calls);
        Assert.Equal(client.Arguments, launcher.Arguments);
        Assert.Equal(client.OperationId, Assert.IsType<CompleteFirewallAccessRequest>(client.Completion).OperationId);
        Assert.False(Assert.IsType<CompleteFirewallAccessRequest>(client.Completion).Cancelled);
        Assert.Equal(FirewallAccessPhase.Configured, model.FirewallAccess.Phase);
    }

    [Fact]
    public async Task Dismissed_uac_is_reported_as_cancellation_and_never_retried()
    {
        var client = new FirewallClient { CompletePhase = FirewallAccessPhase.NeedsPermission };
        var launcher = new RecordingLauncher(FirewallElevationOutcome.CancelledByUser());
        var model = Model(client, launcher);

        await model.ConfirmFirewallAccessCommand.ExecuteAsync(null);

        var completion = Assert.IsType<CompleteFirewallAccessRequest>(client.Completion);
        Assert.True(completion.Cancelled);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains("nothing changed", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirewallAccessPhase.NeedsPermission, model.FirewallAccess.Phase);
    }

    [Fact]
    public async Task Failed_removal_offers_one_retry_that_prepares_remove_not_repair()
    {
        var client = new FirewallClient { CompletePhase = FirewallAccessPhase.NeedsPermission };
        var launcher = new RecordingLauncher(FirewallElevationOutcome.Completed(0, "removed"));
        var model = Model(client, launcher);
        model.FirewallAccess = client.State(FirewallAccessPhase.NeedsAttention) with
        {
            Configured = true,
            RemovalPending = true
        };

        Assert.Equal("Retry firewall removal", model.FirewallPrimaryActionText);
        Assert.False(model.ShowsFirewallRemoveAction);
        await model.ConfirmFirewallAccessCommand.ExecuteAsync(null);

        Assert.Equal(FirewallHelperOperation.Remove,
            Assert.IsType<PrepareFirewallAccessRequest>(client.Preparation).Operation);
        Assert.Equal(1, launcher.Calls);
    }

    [Fact]
    public async Task Typed_recovery_actions_execute_real_local_navigation_or_read_only_refresh()
    {
        var client = new FirewallClient();
        var launcher = new RecordingLauncher(FirewallElevationOutcome.Completed(0, "unused"));
        var model = Model(client, launcher);
        model.FirewallAccess = client.State(FirewallAccessPhase.RuntimeUnavailable) with
        {
            Failure = FirewallAccessFailure.RuntimeUnavailable,
            ProgramPath = "",
            JavaVerified = false
        };

        await model.ExecuteFirewallPrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(ServerDestination.Settings, model.Navigation.CurrentServerDestination);
        Assert.Equal(0, launcher.Calls);

        model.FirewallAccess = client.State(FirewallAccessPhase.NetworkUnavailable) with
        {
            Failure = FirewallAccessFailure.NetworkUnavailable,
            TargetProblem = FirewallTargetProblem.NetworkProfileUnavailable
        };
        await model.ExecuteFirewallPrimaryActionCommand.ExecuteAsync(null);

        Assert.Contains("CheckFirewallAccess", client.Operations);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task View_details_and_try_again_do_not_elevate_until_separate_confirmation()
    {
        var client = new FirewallClient();
        var launcher = new RecordingLauncher(FirewallElevationOutcome.Completed(0, "unused"));
        var model = Model(client, launcher);
        model.FirewallAccess = client.State(FirewallAccessPhase.BlockedByPolicy) with
        {
            BlockingRuleName = "Administrator block"
        };

        await model.ExecuteFirewallPrimaryActionCommand.ExecuteAsync(null);
        Assert.True(model.ShowsFirewallTechnicalDetails);
        Assert.Equal(0, launcher.Calls);

        model.FirewallAccess = client.State(FirewallAccessPhase.NeedsPermission) with
        {
            Failure = FirewallAccessFailure.Cancelled
        };
        await model.ExecuteFirewallPrimaryActionCommand.ExecuteAsync(null);
        Assert.True(model.ShowsFirewallConsent);
        Assert.Equal(0, launcher.Calls);
    }

    private static MainViewModel Model(FirewallClient client, RecordingLauncher launcher)
    {
        var id = client.ServerId;
        var server = new ServerSnapshot
        {
            Definition = new ServerDefinition
            {
                Id = id,
                Name = "Firewall fixture",
                RootPath = @"D:\ChunkPilot\Servers\Firewall-fixture",
                Executable = client.Java,
                Port = 25566
            },
            State = ServerState.Stopped
        };
        var state = client.State(FirewallAccessPhase.NeedsPermission);
        var model = new MainViewModel(client, new SilentDialogs(), () => launcher);
        model.SetFirewallReviewState(server, new RouterMappingState(), state, false);
        client.Operations.Clear();
        return model;
    }

    private sealed class RecordingLauncher(FirewallElevationOutcome outcome) : IFirewallElevationLauncher
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<FirewallElevationOutcome> LaunchAsync(
            IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            Arguments = arguments.ToArray();
            return Task.FromResult(outcome);
        }
    }

    private sealed class FirewallClient : IAgentClient
    {
        public Guid ServerId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = Guid.NewGuid();
        public string Java { get; } = @"D:\ChunkPilot\ManagedJava\temurin-21\bin\java.exe";
        public IReadOnlyList<string> Arguments { get; } = ["--operation", "create", "--agent-token", "opaque"];
        public List<string> Operations { get; } = [];
        public object? Completion { get; private set; }
        public object? Preparation { get; private set; }
        public FirewallAccessPhase CompletePhase { get; init; } = FirewallAccessPhase.Configured;

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            object response = operation switch
            {
                "PrepareFirewallAccess" => Prepare(payload),
                "CompleteFirewallAccess" => Complete(payload),
                "CheckFirewallAccess" => State(FirewallAccessPhase.NeedsPermission),
                "SetSetting" => OperationResult.Ok("fixture"),
                _ => throw new InvalidOperationException($"Unexpected operation {operation}.")
            };
            return Task.FromResult((TResponse)response);
        }

        private WindowsFirewallState Complete(object? payload)
        {
            Completion = payload;
            return State(CompletePhase) with { Configured = CompletePhase == FirewallAccessPhase.Configured };
        }

        private FirewallElevationTicket Prepare(object? payload)
        {
            Preparation = payload;
            var request = Assert.IsType<PrepareFirewallAccessRequest>(payload);
            return new FirewallElevationTicket
            {
                Ready = true,
                OperationId = OperationId,
                Operation = request.Operation,
                Arguments = Arguments,
                State = State(FirewallAccessPhase.WaitingForElevation) with { Busy = true }
            };
        }

        public WindowsFirewallState State(FirewallAccessPhase phase) => new()
        {
            ServerId = ServerId,
            Phase = phase,
            ProgramPath = Java,
            JavaVerified = true,
            Port = 25566,
            PortVerified = true,
            Profiles = FirewallProfile.Private,
            SelectedProfile = FirewallProfile.Private,
            NetworkProfileVerified = true,
            Category = WindowsNetworkCategory.Private,
            FirewallApiAvailable = true,
            FirewallEnabledForProfile = true,
            ModifyState = FirewallPolicyModifyState.Ok
        };
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => true;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
