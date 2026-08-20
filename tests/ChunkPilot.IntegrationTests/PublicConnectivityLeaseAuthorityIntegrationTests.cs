using ChunkPilot.Agent;
using ChunkPilot.Core;

namespace ChunkPilot.IntegrationTests;

public sealed class PublicConnectivityLeaseAuthorityIntegrationTests
{
    [Fact]
    public void Current_exact_session_can_create_independent_server_leases()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var clock = new ManualClock();
        var sessions = new UiSessionAuthority(observer, clock, TimeSpan.FromSeconds(3));
        var registration = sessions.Register(new UiSessionRegistrationRequest(41, 9001));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions, clock);

        var alpha = leases.Create(Guid.NewGuid(), credential);
        var beta = leases.Create(Guid.NewGuid(), credential);

        Assert.NotEqual(alpha.LeaseId, beta.LeaseId);
        Assert.Equal(1, alpha.Generation);
        Assert.Equal(1, beta.Generation);
        leases.Demand(alpha.ServerId, credential, alpha, "test");
        leases.Demand(beta.ServerId, credential, beta, "test");
    }

    [Fact]
    public void Missing_refused_wrong_session_and_wrong_generation_cannot_mutate_authority()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var sessions = new UiSessionAuthority(observer);
        var registration = sessions.Register(new UiSessionRegistrationRequest(42, 9002));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        var serverId = Guid.NewGuid();
        var lease = leases.Create(serverId, credential);

        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Demand(serverId, new UiSessionCredential(), lease, "missing"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Demand(serverId, credential with { Capability = Convert.ToBase64String(new byte[32]) },
                lease, "wrong capability"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Demand(serverId, credential with { SessionId = Guid.NewGuid() }, lease, "wrong session"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Demand(serverId, credential, lease with { Generation = lease.Generation + 1 }, "stale"));

        Assert.Equal(lease, leases.Get(serverId));
    }

    [Fact]
    public void Revoked_or_replayed_generation_cannot_mutate_fresh_generation()
    {
        var sessions = new UiSessionAuthority(new MutableObserver(UiProcessLiveness.Alive));
        var registration = sessions.Register(new UiSessionRegistrationRequest(43, 9003));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        var serverId = Guid.NewGuid();
        var first = leases.Create(serverId, credential);

        Assert.Equal(first, leases.Revoke(serverId, credential, first, "disable"));
        var second = leases.Create(serverId, credential);

        Assert.True(second.Generation > first.Generation);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Demand(serverId, credential, first, "late cleanup"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            leases.Revoke(serverId, credential, first, "replay"));
        Assert.Equal(second, leases.Get(serverId));
    }

    [Fact]
    public void Agent_restart_registry_inherits_no_lease_or_capability()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var firstSessions = new UiSessionAuthority(observer);
        var firstRegistration = firstSessions.Register(new UiSessionRegistrationRequest(44, 9004));
        var oldCredential = Credential(firstRegistration);
        var serverId = Guid.NewGuid();
        var oldLease = new PublicConnectivityLeaseRegistry(firstSessions).Create(serverId, oldCredential);

        var restartedSessions = new UiSessionAuthority(observer);
        var restartedLeases = new PublicConnectivityLeaseRegistry(restartedSessions);

        Assert.False(restartedLeases.Get(serverId).IsPresent);
        Assert.Throws<UnauthorizedAccessException>(() =>
            restartedLeases.Demand(serverId, oldCredential, oldLease, "restart replay"));
    }

    [Fact]
    public void Application_exit_epoch_atomically_rejects_registration_and_exposure_mutation()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var sessions = new UiSessionAuthority(observer);
        var registration = sessions.Register(new UiSessionRegistrationRequest(48, 9008));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        var lease = leases.Create(Guid.NewGuid(), credential);

        var exitEpoch = sessions.BeginApplicationExit(credential);
        var revoked = leases.RevokeAll();

        Assert.Equal(lease.LifecycleEpoch, exitEpoch);
        Assert.Single(revoked);
        Assert.True(leases.IsExactRevokedGeneration(lease, exitEpoch));
        Assert.Throws<InvalidOperationException>(() =>
            sessions.Register(new UiSessionRegistrationRequest(49, 9009)));
        Assert.Throws<UnauthorizedAccessException>(() => leases.Create(Guid.NewGuid(), credential));
        Assert.False(sessions.IsExposureEpochCurrent(exitEpoch));
    }

    [Fact]
    public void Old_cleanup_is_rejected_after_a_hypothetical_newer_generation()
    {
        var sessions = new UiSessionAuthority(new MutableObserver(UiProcessLiveness.Alive));
        var registration = sessions.Register(new UiSessionRegistrationRequest(50, 9010));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        var serverId = Guid.NewGuid();
        var first = leases.Create(serverId, credential);
        _ = leases.Revoke(serverId, credential, first, "first cleanup");
        var second = leases.Create(serverId, credential);

        Assert.False(leases.IsLatestRevokedGeneration(first));
        Assert.True(leases.IsCurrent(second));
    }

    [Fact]
    public void Application_exit_snapshot_includes_an_already_revoked_generation_with_pending_cleanup()
    {
        var sessions = new UiSessionAuthority(new MutableObserver(UiProcessLiveness.Alive));
        var registration = sessions.Register(new UiSessionRegistrationRequest(51, 9011));
        var credential = Credential(registration);
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        var serverId = Guid.NewGuid();
        var lease = leases.Create(serverId, credential);

        _ = leases.Revoke(serverId, credential, lease, "manual cleanup pending");
        var exitEpoch = sessions.BeginApplicationExit(credential);
        var snapshot = leases.RevokeAll();

        Assert.Equal(lease.LifecycleEpoch, exitEpoch);
        Assert.Contains(lease, snapshot);
        Assert.True(leases.IsExactRevokedGeneration(lease, exitEpoch));
    }

    [Fact]
    public void Exact_process_death_and_pid_reuse_are_gone_but_live_pipe_gaps_are_irrelevant()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var sessions = new UiSessionAuthority(observer);
        _ = sessions.Register(new UiSessionRegistrationRequest(45, 9005));

        Assert.Equal(UiSessionObservation.Alive, sessions.Observe());
        observer.Liveness = UiProcessLiveness.Gone;
        Assert.Equal(UiSessionObservation.GoneOrUnprovable, sessions.Observe());

        // The observer returning Gone for the exact PID/creation pair is also how a reused PID with a
        // different creation identity is represented. No named-pipe state participates in this result.
        Assert.NotNull(sessions.EndObservedSession());
        Assert.Equal(UiSessionObservation.None, sessions.Observe());
    }

    [Fact]
    public void Unknown_uses_one_bounded_monotonic_deadline_and_recovers_when_alive()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var clock = new ManualClock();
        var sessions = new UiSessionAuthority(observer, clock, TimeSpan.FromSeconds(3));
        _ = sessions.Register(new UiSessionRegistrationRequest(46, 9006));

        observer.Liveness = UiProcessLiveness.Unknown;
        Assert.Equal(UiSessionObservation.UnknownWithinDeadline, sessions.Observe());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(UiSessionObservation.UnknownWithinDeadline, sessions.Observe());

        observer.Liveness = UiProcessLiveness.Alive;
        Assert.Equal(UiSessionObservation.Alive, sessions.Observe());
        observer.Liveness = UiProcessLiveness.Unknown;
        Assert.Equal(UiSessionObservation.UnknownWithinDeadline, sessions.Observe());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(UiSessionObservation.GoneOrUnprovable, sessions.Observe());
    }

    [Fact]
    public void Repeated_observer_failure_fails_closed_after_the_same_deadline()
    {
        var observer = new MutableObserver(UiProcessLiveness.Alive);
        var clock = new ManualClock();
        var sessions = new UiSessionAuthority(observer, clock, TimeSpan.FromSeconds(2));
        _ = sessions.Register(new UiSessionRegistrationRequest(47, 9007));

        observer.Throw = true;
        Assert.Equal(UiSessionObservation.UnknownWithinDeadline, sessions.Observe());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(UiSessionObservation.GoneOrUnprovable, sessions.Observe());
    }

    [Fact]
    public void Production_observer_uses_exact_current_windows_process_identity()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var creation = ProcessCreationIdentity.OfCurrentProcess();
        Assert.NotEqual(ProcessCreationIdentity.Unknown, creation);
        var observer = new SystemUiProcessObserver();

        Assert.Equal(UiProcessLiveness.Alive, observer.Observe(Environment.ProcessId, creation));
        Assert.Equal(UiProcessLiveness.Gone, observer.Observe(Environment.ProcessId, creation + 1));
    }

    private static UiSessionCredential Credential((ApplicationSession Session, string Capability) registration) =>
        new() { SessionId = registration.Session.SessionId, Capability = registration.Capability };

    private sealed class MutableObserver(UiProcessLiveness liveness) : IUiProcessObserver
    {
        public UiProcessLiveness Liveness { get; set; } = liveness;
        public bool Throw { get; set; }

        public UiProcessLiveness Observe(int processId, long creationTicks)
        {
            if (Throw)
                throw new InvalidOperationException("synthetic observer failure");
            return Liveness;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utc = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan elapsed)
        {
            timestamp += elapsed.Ticks;
            utc += elapsed;
        }
    }
}
