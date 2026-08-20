using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// The truthfulness boundary, enforced as tests. A completed TCP handshake proves the public network
/// path reached the listening port and nothing further; the copy has to stop exactly there.
/// </summary>
public sealed class ExternalReachabilityPresentationTests
{
    private const string Public = "93.184.216.34";

    [Fact]
    public void Every_state_has_a_title_a_summary_and_a_badge()
    {
        foreach (var phase in Enum.GetValues<ExternalReachabilityPhase>())
        {
            var state = new ExternalReachabilityState { Phase = phase, Port = 25566 };
            Assert.NotEqual("", ExternalReachabilityPresentation.Title(state));
            Assert.NotEqual("", ExternalReachabilityPresentation.Summary(state, Router(), Firewall()));
            Assert.NotEqual("", ExternalReachabilityPresentation.Badge(state));
            Assert.NotEqual("", ExternalReachabilityPresentation.ActionText(state));
        }
    }

    [Fact]
    public void Every_missing_prerequisite_is_explained_rather_than_called_a_failed_check()
    {
        foreach (var blocker in Enum.GetValues<ExternalReachabilityBlocker>())
        {
            var state = new ExternalReachabilityState
            {
                Phase = ExternalReachabilityPhase.Ineligible,
                Blocker = blocker,
                Port = 25566
            };
            var copy = $"{ExternalReachabilityPresentation.Title(state)} " +
                       $"{ExternalReachabilityPresentation.Summary(state, Router(), Firewall())}";

            Assert.NotEqual("", copy.Trim());
            foreach (var forbidden in new[] { "unreachable", "could not reach", "failed" })
                Assert.DoesNotContain(forbidden, copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The whole point of this milestone: a verified result must not be allowed to grow into a
    /// promise about joining the game.
    /// </summary>
    [Fact]
    public void A_verified_result_claims_the_tcp_path_and_nothing_beyond_it()
    {
        var state = Verified();

        var copy = $"{ExternalReachabilityPresentation.Title(state)} " +
                   $"{ExternalReachabilityPresentation.Summary(state, Router(), Firewall())}";

        Assert.Contains("Public access verified", copy, StringComparison.Ordinal);
        Assert.Contains("TCP 25566 answered from outside your network", copy, StringComparison.Ordinal);
        foreach (var overclaim in new[]
                 {
                     "every friend", "anyone can join", "will be able to join", "guaranteed",
                     "definitely", "whitelist is", "version match", "compatible"
                 })
            Assert.DoesNotContain(overclaim, copy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_state_other_than_reachable_may_use_verified_wording()
    {
        foreach (var phase in Enum.GetValues<ExternalReachabilityPhase>()
                     .Where(value => value != ExternalReachabilityPhase.Reachable))
        {
            var state = Verified() with { Phase = phase };
            var title = ExternalReachabilityPresentation.Title(state);
            Assert.DoesNotContain("Public access verified", title, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A failed handshake means the probe could not establish TCP. Nothing else may be asserted, and
    /// nothing may be named as the cause.
    /// </summary>
    [Fact]
    public void An_unreachable_result_never_asserts_a_cause()
    {
        var state = new ExternalReachabilityState
        {
            Phase = ExternalReachabilityPhase.Unreachable,
            Port = 25566,
            ProbeConfigured = true
        };

        var summary = ExternalReachabilityPresentation.Summary(state, Router(), Firewall());

        foreach (var certainty in new[] { "is blocking", "because your", "CGNAT", "your provider is" })
            Assert.DoesNotContain(certainty, summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_configured_router_and_firewall_with_no_answer_says_exactly_that()
    {
        var state = new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Unreachable, Port = 25566 };

        var summary = ExternalReachabilityPresentation.Summary(state, Router(), Firewall());

        Assert.Contains("router and Windows Firewall look configured", summary, StringComparison.Ordinal);
        Assert.Contains("may be", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unconfirmed_firewall_is_named_as_the_next_thing_to_settle()
    {
        var state = new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Unreachable, Port = 25566 };

        var summary = ExternalReachabilityPresentation.Summary(state, Router(),
            Firewall(FirewallAccessPhase.NeedsPermission));

        Assert.Contains("Windows Firewall access isn't confirmed", summary, StringComparison.Ordinal);
    }

    /// <summary>An exact foreign allow or a disabled firewall is settled evidence, not a gap.</summary>
    [Theory]
    [InlineData(FirewallAccessPhase.Configured)]
    [InlineData(FirewallAccessPhase.ExistingWindowsRule)]
    [InlineData(FirewallAccessPhase.FirewallDisabled)]
    public void Settled_firewall_evidence_of_any_kind_produces_the_same_composed_diagnosis(
        FirewallAccessPhase phase)
    {
        var state = new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Unreachable, Port = 25566 };

        Assert.Contains("router and Windows Firewall look configured",
            ExternalReachabilityPresentation.Summary(state, Router(), Firewall(phase)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_mismatch_is_never_reported_as_unreachable()
    {
        var state = new ExternalReachabilityState
        {
            Phase = ExternalReachabilityPhase.SourceAddressMismatch,
            RouterReportedAddress = Public,
            ObservedAddress = "198.51.100.42",
            Port = 25566
        };

        var copy = $"{ExternalReachabilityPresentation.Title(state)} " +
                   $"{ExternalReachabilityPresentation.Summary(state, Router(), Firewall())}";

        Assert.Equal("Different public address detected", ExternalReachabilityPresentation.Title(state));
        Assert.DoesNotContain("could not reach", copy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VPN", copy, StringComparison.Ordinal);
        Assert.Contains(Public, ExternalReachabilityPresentation.AddressComparison(state), StringComparison.Ordinal);
        Assert.Contains("198.51.100.42", ExternalReachabilityPresentation.AddressComparison(state),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_service_problem_is_never_reported_as_unreachable()
    {
        foreach (var phase in new[]
                 {
                     ExternalReachabilityPhase.ProbeUnavailable, ExternalReachabilityPhase.RateLimited,
                     ExternalReachabilityPhase.Cancelled
                 })
        {
            var state = new ExternalReachabilityState { Phase = phase, ProbeConfigured = true, Port = 25566 };
            var copy = $"{ExternalReachabilityPresentation.Title(state)} " +
                       $"{ExternalReachabilityPresentation.Summary(state, Router(), Firewall())}";

            Assert.DoesNotContain("could not reach this server", copy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not changed", copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── CGNAT and double NAT ──

    [Fact]
    public void Shared_address_space_is_stated_as_evidence_rather_than_guessed()
    {
        var router = Router() with
        {
            RouterReportedExternalAddress = "100.64.9.12",
            RouterReportedAddressClass = RoutableAddressClass.SharedAddressSpace
        };

        var assessment = ExternalReachabilityPresentation.UpstreamAssessment(
            new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Ineligible }, router);

        Assert.Contains("Router does not have a public address", assessment, StringComparison.Ordinal);
        Assert.Contains("carrier-grade NAT", assessment, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_router_address_says_another_layer_rather_than_naming_cgnat()
    {
        var router = Router() with
        {
            RouterReportedExternalAddress = "192.168.100.1",
            RouterReportedAddressClass = RoutableAddressClass.PrivateUse
        };

        var assessment = ExternalReachabilityPresentation.UpstreamAssessment(
            new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Ineligible }, router);

        Assert.Contains("another network layer", assessment, StringComparison.Ordinal);
        Assert.DoesNotContain("carrier-grade", assessment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A timeout proves nothing about NAT, so it must produce no upstream claim at all.</summary>
    [Fact]
    public void A_failed_connection_alone_never_produces_an_upstream_nat_claim()
    {
        var state = new ExternalReachabilityState { Phase = ExternalReachabilityPhase.Unreachable };

        Assert.Equal("", ExternalReachabilityPresentation.UpstreamAssessment(state, Router()));
    }

    [Fact]
    public void A_mismatch_on_a_public_router_address_is_offered_as_a_possibility()
    {
        var state = new ExternalReachabilityState { Phase = ExternalReachabilityPhase.SourceAddressMismatch };

        var assessment = ExternalReachabilityPresentation.UpstreamAssessment(state, Router());

        Assert.StartsWith("Possible upstream NAT", assessment, StringComparison.Ordinal);
    }

    // ── Tone, badges and the first-use notice ──

    [Fact]
    public void Only_a_verified_result_earns_a_success_tone()
    {
        foreach (var phase in Enum.GetValues<ExternalReachabilityPhase>())
        {
            var state = new ExternalReachabilityState { Phase = phase };
            var tone = ExternalReachabilityPresentation.Tone(state);
            if (phase == ExternalReachabilityPhase.Reachable)
                Assert.Equal(AppTone.Success, tone);
            else
                Assert.NotEqual(AppTone.Success, tone);
        }
    }

    /// <summary>Status must never be carried by colour alone, so every state has a distinct word.</summary>
    [Fact]
    public void The_badge_distinguishes_the_states_that_look_different_to_a_user()
    {
        var badges = Enum.GetValues<ExternalReachabilityPhase>()
            .Select(phase => ExternalReachabilityPresentation.Badge(
                new ExternalReachabilityState { Phase = phase }))
            .ToArray();

        Assert.All(badges, badge => Assert.NotEqual("", badge));
        // Only NotChecked and Ineligible deliberately share a word: neither has been checked.
        Assert.Equal(badges.Length - 1, badges.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_first_use_notice_names_what_is_sent_and_what_is_not()
    {
        var notice = ExternalReachabilityPresentation.FirstUseNotice(
            new ExternalReachabilityState { Port = 25566, ProbeConfigured = true });

        Assert.Contains("25566", notice, StringComparison.Ordinal);
        Assert.Contains("public address the probe sees", notice, StringComparison.Ordinal);
        Assert.Contains("No world, player, or server files are sent", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Point_in_time_evidence_is_always_labelled_with_its_time()
    {
        var state = Verified() with { CheckedAt = new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero) };

        Assert.StartsWith("Verified ", ExternalReachabilityPresentation.VerifiedAt(state), StringComparison.Ordinal);
        Assert.Equal("", ExternalReachabilityPresentation.VerifiedAt(state with { CheckedAt = null }));
        Assert.Equal("Never",
            ExternalReachabilityPresentation.CheckedAtLabel(state with { CheckedAt = null }));
    }

    /// <summary>Protocol and provider names are never part of the everyday copy.</summary>
    [Fact]
    public void Primary_copy_names_no_provider_and_no_protocol_machinery()
    {
        foreach (var phase in Enum.GetValues<ExternalReachabilityPhase>())
        {
            var state = new ExternalReachabilityState { Phase = phase, Port = 25566, ProbeConfigured = true };
            var copy = $"{ExternalReachabilityPresentation.Title(state)} " +
                       $"{ExternalReachabilityPresentation.Summary(state, Router(), Firewall())} " +
                       $"{ExternalReachabilityPresentation.Badge(state)}";

            foreach (var word in new[] { "Cloudflare", "Worker", "HTTPS", "JSON", "socket", "API", "handshake" })
                Assert.DoesNotContain(word, copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ExternalReachabilityState Verified() => new()
    {
        Phase = ExternalReachabilityPhase.Reachable,
        ProbeConfigured = true,
        ObservedAddress = Public,
        RouterReportedAddress = Public,
        Port = 25566,
        ConnectMilliseconds = 118,
        CheckedAt = new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero)
    };

    private static RouterMappingState Router() => new()
    {
        Enabled = true,
        Phase = RouterMappingPhase.Active,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        InternalClient = "10.0.0.140",
        InternalPort = 25566,
        ExternalPort = 25566,
        RouterReportedExternalAddress = Public,
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable
    };

    private static WindowsFirewallState Firewall(
        FirewallAccessPhase phase = FirewallAccessPhase.Configured) => new()
    {
        Phase = phase,
        Configured = phase == FirewallAccessPhase.Configured,
        Port = 25566
    };
}
