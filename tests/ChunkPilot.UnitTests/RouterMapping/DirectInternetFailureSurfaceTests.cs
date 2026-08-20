using System.ComponentModel;
using System.Reflection;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// What the Direct internet surface must say after a real router attempt, and the notification
/// contract that keeps it saying it.
/// </summary>
/// <remarks>
/// The real-router acceptance run produced a screen that claimed no gateway had been identified while
/// quoting the gateway's own answer two lines below, and offered "Not set up" after a check that had
/// succeeded. Both halves are pinned here.
/// </remarks>
public sealed class DirectInternetFailureSurfaceTests
{
    private static readonly Guid ServerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// The defect that made the diagnostic screen contradict itself: two technical rows were computed
    /// from the state but never told it had changed, so they kept their first-bound values.
    /// </summary>
    [Fact]
    public void Every_property_that_changes_with_the_state_raises_a_change_notification()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs());
        var raised = new List<string>();
        model.PropertyChanged += (_, args) => raised.Add(args.PropertyName ?? "");

        var before = Snapshot(model);
        model.RouterMapping = Answered();
        var after = Snapshot(model);

        var changed = after
            .Where(entry => !Equals(entry.Value, before.GetValueOrDefault(entry.Key)))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.NotEmpty(changed);
        var unannounced = changed.Where(name => !raised.Contains(name)).ToArray();
        Assert.True(unannounced.Length == 0,
            "These properties changed without a notification, so the screen would keep stale values: " +
            string.Join(", ", unannounced));
    }

    /// <summary>The exact rows the real screenshot got wrong.</summary>
    [Fact]
    public void The_technical_rows_follow_the_state_instead_of_keeping_their_first_values()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs());
        Assert.Equal("Not identified", model.DirectInternetGateway);

        model.RouterMapping = Answered();

        Assert.Equal("10.0.0.1", model.DirectInternetGateway);
        Assert.Equal("10.0.0.23:25565 (candidate, not mapped)", model.DirectInternetInternalEndpoint);
        Assert.Equal("UPnP IGD — answered, no mapping created", model.DirectInternetMechanismLabel);
        Assert.NotEqual("Never", model.DirectInternetLastCheckedLabel);
    }

    /// <summary>A router that answered must never be summarised as "Not set up".</summary>
    [Fact]
    public void A_router_that_answered_is_not_described_as_not_set_up()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs()) { RouterMapping = Answered() };

        Assert.Equal("Your router can be set up automatically", model.DirectInternetTitle);
        Assert.NotEqual("Not set up", model.DirectInternetTitle);
        Assert.Equal("Ready", model.DirectInternetBadge);
    }

    [Fact]
    public void The_address_the_router_reported_is_shown_before_any_port_exists()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs()) { RouterMapping = Answered() };

        Assert.True(model.ShowsRouterReportedAddress);
        Assert.Equal("73.203.43.174", model.RouterReportedEndpoint);
        Assert.NotEqual("", model.RouterReportedEndpoint);
    }

    [Theory]
    [InlineData(RouterMappingPhase.NeedsAttention, RouterMappingFailure.RequestRejected, "turned down the request")]
    [InlineData(RouterMappingPhase.NeedsAttention, RouterMappingFailure.NotAuthorized, "refused the request")]
    [InlineData(RouterMappingPhase.NeedsAttention, RouterMappingFailure.OutOfResources, "no room for another entry")]
    [InlineData(RouterMappingPhase.NeedsAttention, RouterMappingFailure.NetworkFailure, "network problem")]
    [InlineData(RouterMappingPhase.Undetermined, RouterMappingFailure.GatewayDidNotRespond, "didn't answer")]
    [InlineData(RouterMappingPhase.Unavailable, RouterMappingFailure.MechanismUnsupported, "didn't offer automatic")]
    [InlineData(RouterMappingPhase.Unavailable, RouterMappingFailure.NoGatewayFound, "safe local address")]
    public void Every_failed_attempt_explains_itself_and_offers_a_retry(
        RouterMappingPhase phase, RouterMappingFailure failure, string expected)
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs())
        {
            RouterMapping = Answered() with { Phase = phase, Failure = failure, Enabled = true }
        };

        Assert.Contains(expected, model.DirectInternetSummary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Not set up", model.DirectInternetTitle);
        Assert.NotEqual(AppTone.Neutral, model.DirectInternetTone);
        // A failure the user can act on keeps both a retry and a way out.
        Assert.True(model.ShowsDirectInternetPrimaryAction);
        Assert.Equal("Check again", model.DirectInternetPrimaryActionText);
        Assert.True(model.ShowsDirectInternetTurnOff);
    }

    /// <summary>A failure keeps the router's own words available without putting XML on the page.</summary>
    [Fact]
    public void A_failure_keeps_the_protocol_evidence_under_technical_details()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs())
        {
            RouterMapping = Answered() with
            {
                Phase = RouterMappingPhase.NeedsAttention,
                Failure = RouterMappingFailure.RequestRejected,
                Enabled = true,
                LastOperationDetail =
                    "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs)."
            }
        };

        Assert.Contains("402", model.DirectInternetTechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("InvalidArgs", model.DirectInternetTechnicalDetail, StringComparison.Ordinal);
        Assert.Equal("10.0.0.1", model.DirectInternetGateway);
        // The primary copy stays plain; no envelope, no element names.
        Assert.DoesNotContain("<", model.DirectInternetSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Envelope", model.DirectInternetSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("402", model.DirectInternetSummary, StringComparison.Ordinal);
    }

    /// <summary>A mapping that never existed must not be described as having a lease.</summary>
    [Fact]
    public void A_failed_attempt_reports_no_lease_and_no_external_port()
    {
        var model = new MainViewModel(new SilentClient(), new SilentDialogs())
        {
            RouterMapping = Answered() with
            {
                Phase = RouterMappingPhase.NeedsAttention,
                Failure = RouterMappingFailure.RequestRejected,
                Enabled = true
            }
        };

        Assert.Equal("Not established", model.DirectInternetLeaseLabel);
        Assert.Equal("—", model.DirectInternetExternalPortLabel);
    }

    private static RouterMappingState Answered() => new()
    {
        ServerId = ServerId,
        Phase = RouterMappingPhase.Supported,
        Mechanism = RouterMappingMechanism.None,
        AvailableMechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        GatewayAddress = "10.0.0.1",
        CandidateInternalClient = "10.0.0.23",
        InternalPort = 25565,
        RouterReportedExternalAddress = "73.203.43.174",
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
        LastCheckedAt = DateTimeOffset.Now,
        LastOperationDetail =
            "UPnP urn:schemas-upnp-org:service:WANIPConnection:1 answered at " +
            "http://10.0.0.1:49152/upnp/control/WANIPConnection0 and reported external address 73.203.43.174."
    };

    /// <summary>
    /// Reads every scalar property that can be read without side effects. Collection-valued views are
    /// skipped: several are materialised fresh on each read, so they always compare unequal and say
    /// nothing about whether a displayed value went stale.
    /// </summary>
    private static Dictionary<string, object?> Snapshot(MainViewModel model)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in typeof(MainViewModel)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            try
            {
                var value = property.GetValue(model);
                if (value is System.Collections.IEnumerable and not string)
                    continue;
                values[property.Name] = value;
            }
            catch (TargetInvocationException)
            {
                // A property that needs a selected server is not part of this contract.
            }
        }
        return values;
    }

    private sealed class SilentClient : IAgentClient
    {
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This surface test never contacts the agent.");
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
