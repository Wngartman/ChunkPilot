using System.Security.Cryptography;
using ChunkPilot.Core;

namespace ChunkPilot.Agent;

public enum UiSessionObservation
{
    None,
    Alive,
    UnknownWithinDeadline,
    GoneOrUnprovable
}

/// <summary>
/// Agent-only authority for the one exact UI process. Capabilities are never persisted and therefore
/// cannot survive an Agent restart or transfer to a replacement process.
/// </summary>
public sealed class UiSessionAuthority
{
    private static readonly TimeSpan DefaultUnknownDeadline = TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private readonly IUiProcessObserver observer;
    private readonly TimeProvider clock;
    private readonly TimeSpan unknownDeadline;
    private ActiveSession? active;
    private long? unknownSince;
    private long lifecycleEpoch = 1;
    private bool applicationExitStarted;

    public UiSessionAuthority(
        IUiProcessObserver observer,
        TimeProvider? clock = null,
        TimeSpan? unknownDeadline = null)
    {
        this.observer = observer;
        this.clock = clock ?? TimeProvider.System;
        this.unknownDeadline = unknownDeadline ?? DefaultUnknownDeadline;
    }

    public (ApplicationSession Session, string Capability) Register(UiSessionRegistrationRequest request)
    {
        var liveness = ObserveSafely(request.ProcessId, request.ProcessCreationTicks);
        if (liveness != UiProcessLiveness.Alive)
            throw new UnauthorizedAccessException(
                "ChunkPilot could not prove the exact UI process that requested this session.");

        lock (sync)
        {
            if (applicationExitStarted)
                throw new InvalidOperationException(
                    "This Agent is completing safe application exit. Reconnect to the next Agent instance.");
            if (active is not null)
                throw new InvalidOperationException(
                    "Another authenticated ChunkPilot UI session is still active. Wait for its safe exit to finish.");

            var session = new ApplicationSession
            {
                SessionId = Guid.NewGuid(),
                StartedAt = clock.GetUtcNow(),
                LastHeartbeatAt = clock.GetUtcNow(),
                ProcessId = request.ProcessId,
                ProcessCreationTicks = request.ProcessCreationTicks
            };
            var capabilityBytes = RandomNumberGenerator.GetBytes(32);
            var capability = Convert.ToBase64String(capabilityBytes);
            active = new ActiveSession(session, capabilityBytes, capability);
            unknownSince = null;
            return (session, capability);
        }
    }

    public ApplicationSession? CurrentSession
    {
        get
        {
            lock (sync)
                return active?.Session;
        }
    }

    public long LifecycleEpoch
    {
        get
        {
            lock (sync)
                return lifecycleEpoch;
        }
    }

    public bool ApplicationExitStarted
    {
        get
        {
            lock (sync)
                return applicationExitStarted;
        }
    }

    public bool IsAuthorized(UiSessionCredential credential)
    {
        if (!credential.IsPresent)
            return false;
        lock (sync)
            return !applicationExitStarted && active is { } current && Authorizes(current, credential);
    }

    public void Demand(UiSessionCredential credential, string operation)
    {
        if (!IsAuthorized(credential))
            throw new UnauthorizedAccessException(
                $"{operation} was refused because its UI session capability is missing, stale, or belongs to another process.");
    }

    public bool End(UiSessionCredential credential)
    {
        lock (sync)
        {
            if (active is not { } current || !Authorizes(current, credential))
                return false;
            active = null;
            unknownSince = null;
            return true;
        }
    }

    /// <summary>
    /// Atomically fences this Agent lifetime against replacement registration and all capability
    /// mutation, then ends the exact authenticated session. The returned epoch is the only epoch
    /// whose revoked leases may be cleaned up by this exit.
    /// </summary>
    public long BeginApplicationExit(UiSessionCredential credential)
    {
        lock (sync)
        {
            if (applicationExitStarted)
                return lifecycleEpoch;
            if (active is not { } current || !Authorizes(current, credential))
                throw new UnauthorizedAccessException(
                    "Safe application exit lost its current exact session authority.");
            applicationExitStarted = true;
            active = null;
            unknownSince = null;
            return lifecycleEpoch;
        }
    }

    public (ApplicationSession? Session, long Epoch) BeginObservedApplicationExit()
    {
        lock (sync)
        {
            if (applicationExitStarted)
                return (null, lifecycleEpoch);
            applicationExitStarted = true;
            var ended = active?.Session;
            active = null;
            unknownSince = null;
            return (ended, lifecycleEpoch);
        }
    }

    public bool IsExposureEpochCurrent(long expectedEpoch)
    {
        lock (sync)
            return !applicationExitStarted && lifecycleEpoch == expectedEpoch;
    }

    public bool IsExitCleanupEpoch(long expectedEpoch)
    {
        lock (sync)
            return applicationExitStarted && lifecycleEpoch == expectedEpoch;
    }

    public UiSessionObservation Observe()
    {
        ActiveSession? snapshot;
        lock (sync)
            snapshot = active;
        if (snapshot is null)
            return UiSessionObservation.None;

        var liveness = ObserveSafely(snapshot.Session.ProcessId, snapshot.Session.ProcessCreationTicks);
        lock (sync)
        {
            if (!ReferenceEquals(snapshot, active))
                return active is null ? UiSessionObservation.None : UiSessionObservation.Alive;
            if (liveness == UiProcessLiveness.Alive)
            {
                unknownSince = null;
                return UiSessionObservation.Alive;
            }
            if (liveness == UiProcessLiveness.Gone)
                return UiSessionObservation.GoneOrUnprovable;

            var now = clock.GetTimestamp();
            unknownSince ??= now;
            return clock.GetElapsedTime(unknownSince.Value, now) >= unknownDeadline
                ? UiSessionObservation.GoneOrUnprovable
                : UiSessionObservation.UnknownWithinDeadline;
        }
    }

    public ApplicationSession? EndObservedSession()
    {
        lock (sync)
        {
            var ended = active?.Session;
            active = null;
            unknownSince = null;
            return ended;
        }
    }

    private UiProcessLiveness ObserveSafely(int processId, long creationTicks)
    {
        try
        {
            return observer.Observe(processId, creationTicks);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return UiProcessLiveness.Unknown;
        }
    }

    private static bool Authorizes(ActiveSession current, UiSessionCredential credential)
    {
        if (current.Session.SessionId != credential.SessionId)
            return false;
        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(credential.Capability);
        }
        catch (FormatException)
        {
            return false;
        }
        return presented.Length == current.CapabilityBytes.Length &&
               CryptographicOperations.FixedTimeEquals(presented, current.CapabilityBytes);
    }

    private sealed record ActiveSession(
        ApplicationSession Session,
        byte[] CapabilityBytes,
        string Capability);
}
